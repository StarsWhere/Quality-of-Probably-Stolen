[CmdletBinding()]
param(
    [string]$GameDir = $env:PROBABLY_STOLEN_GAME_DIR,
    [string]$FromPackage,
    [switch]$Install,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (!$GameDir) { $GameDir = 'D:\SteamLibrary\steamapps\common\Probably Stolen' }
$game = (Resolve-Path -LiteralPath $GameDir).Path
$exe = Join-Path $game 'Probably Stolen.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Invalid game directory: $game" }

if ($FromPackage) { $FromPackage = (Resolve-Path -LiteralPath $FromPackage).Path }
$manifestSource = if ($FromPackage) { Join-Path $FromPackage 'manifest.xml' } else { Join-Path $root 'assets/manifest.xml' }
$manifest = [xml](Get-Content -LiteralPath $manifestSource -Raw -Encoding utf8)
$id = $manifest.Manifest.ID
if ($id -cne 'starswhere.shopassist') { throw 'Not a Shop Assist package.' }
$version = [version]$manifest.Manifest.ModVersion
$taskRoot = Join-Path $root '.tmp'
$task = Join-Path $taskRoot ("build-shopassist-" + [guid]::NewGuid().ToString('N'))
$package = Join-Path $task 'ShopAssist'
$null = New-Item -ItemType Directory -Path $package
$mutex = [Threading.Mutex]::new($false, 'Local\ProbablyStolen.ShopAssist.Build')
$acquired = $false

function Remove-OwnedDirectory([string]$path, [string]$parent) {
    if (!(Test-Path -LiteralPath $path)) { return }
    $resolved = (Resolve-Path -LiteralPath $path).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $parent -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected cleanup location: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -ErrorAction Stop
}

try {
    try { $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds(60)) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw 'Another Shop Assist build is still running.' }
    if ($FromPackage) {
        foreach ($entry in Get-ChildItem -LiteralPath $FromPackage -Recurse -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source package contains a link.' }
        }
        foreach ($entry in Get-ChildItem -LiteralPath $FromPackage -Force) {
            Copy-Item -LiteralPath $entry.FullName -Destination $package -Recurse
        }
    } else {
        $proj = Join-Path $root 'src/ShopAssist/ShopAssist.csproj'
        & dotnet build $proj -c Release --disable-build-servers --nologo -t:StageMod "-p:GameDir=$game" "-p:Version=$version" "-p:PackageDir=$package"
        if ($LASTEXITCODE -ne 0) { throw 'Staging failed.' }
    }
    foreach ($rel in @('manifest.xml', 'ShopAssist.dll', 'Localization/zh.csv', 'Localization/en.csv')) {
        if (!(Test-Path -LiteralPath (Join-Path $package $rel) -PathType Leaf)) { throw "Missing package file: $rel" }
    }
    $dlls = @(Get-ChildItem -LiteralPath $package -Filter '*.dll' -File -Recurse)
    if ($dlls.Count -ne 1 -or $dlls[0].Name -ne 'ShopAssist.dll') { throw 'Unexpected DLLs in package.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-ModPackage.ps1') -GameDir $game -PackageDir $package
    if ($LASTEXITCODE -ne 0) { throw 'Native package scan failed.' }

    if ($Install) {
        if (Get-Process -Name 'Probably Stolen' -ErrorAction SilentlyContinue | Where-Object Path -eq $exe) {
            throw 'Exit Probably Stolen before replacing the installed package.'
        }
        $mods = (New-Item -ItemType Directory -Path (Join-Path $game 'Mods') -Force).FullName
        $destination = Join-Path $mods 'ShopAssist'
        $tag = [guid]::NewGuid().ToString('N')
        # Keep incomplete copies out of Mods so the native loader never discovers them.
        $incoming = Join-Path $game (".ShopAssist-new-" + $tag)
        $previous = Join-Path $game (".ShopAssist-old-" + $tag)
        if (Test-Path -LiteralPath $destination) {
            $old = [xml](Get-Content -LiteralPath (Join-Path $destination 'manifest.xml') -Raw -Encoding utf8)
            if ($old.Manifest.ID -ne $id -or ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Refusing to replace a different mod: $destination"
            }
            foreach ($entry in Get-ChildItem -LiteralPath $destination -Recurse -Force) {
                $relative = [IO.Path]::GetRelativePath($destination, $entry.FullName)
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                    !(Test-Path -LiteralPath (Join-Path $package $relative))) {
                    throw "Installed folder contains an extra file or link; move it aside first: $relative"
                }
            }
        }
        try {
            Copy-Item -LiteralPath $package -Destination $incoming -Recurse
            if (Test-Path -LiteralPath $destination) {
                if ([IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $destination).Path) -ne $mods) { throw 'Invalid destination.' }
                [IO.Directory]::Move($destination, $previous)
            }
            [IO.Directory]::Move($incoming, $destination)
        } catch {
            if ((Test-Path -LiteralPath $previous) -and !(Test-Path -LiteralPath $destination)) {
                [IO.Directory]::Move($previous, $destination)
            }
            throw
        } finally {
            Remove-OwnedDirectory $incoming $game
        }
        Remove-OwnedDirectory $previous $game
        Write-Output "Installed $id $version -> $destination"
    }
    if ($Release) {
        $releases = Join-Path $root "releases/$version"
        $null = New-Item -ItemType Directory -Path $releases -Force
        $name = "ShopAssist-$version-win64.zip"
        $archive = Join-Path $task $name
        [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
        Copy-Item -LiteralPath $archive -Destination (Join-Path $releases $name) -Force
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
        Set-Content -Encoding ascii -LiteralPath (Join-Path $releases "$name.sha256") -Value "$hash  $name"
        Write-Output "Release -> $(Join-Path $releases $name)"
    }
} finally {
    try { Remove-OwnedDirectory $task $taskRoot }
    finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
