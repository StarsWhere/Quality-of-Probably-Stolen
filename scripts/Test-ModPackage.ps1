[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$PackageDir
)

$ErrorActionPreference = 'Stop'
$managed = (Resolve-Path -LiteralPath (Join-Path $GameDir 'Probably Stolen_Data/Managed')).Path
$package = (Resolve-Path -LiteralPath $PackageDir).Path
foreach ($name in 'UnityEngine.CoreModule.dll','UnityEngine.dll','DseCecil.dll','0Harmony.dll','Assembly-CSharp.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $managed $name))
}

$issue = ''
$manifest = [ModLoader]::ParseManifestForTools($package, [ref]$issue)
if (!$manifest -or $issue) { throw "Invalid manifest: $issue" }
$paths = [Collections.Generic.List[string]]::new()
$names = [Collections.Generic.List[string]]::new()
foreach ($file in $manifest.Assemblies) {
    $path = (Resolve-Path -LiteralPath (Join-Path $package $file)).Path
    if (!$path.StartsWith(($package + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Assembly outside the package: $file"
    }
    $paths.Add($path)
    $names.Add([IO.Path]::GetFileNameWithoutExtension($path))
}
$actual = @(Get-ChildItem -LiteralPath $package -Recurse -File -Filter '*.dll' | ForEach-Object FullName)
if ($actual.Count -ne $paths.Count -or ($actual | Where-Object { $_ -notin $paths })) {
    throw 'The manifest and the DLLs in the package differ.'
}

function Get-LocaleKeys([string]$locale) {
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $package "Localization/$locale.csv"), $utf8)) {
        if (!$line.Trim() -or $line.StartsWith('#')) { continue }
        $pair = $line.Split(',', 2)
        if ($pair.Length -ne 2 -or !$pair[0].Trim() -or !$pair[1].Trim() -or !$keys.Add($pair[0])) {
            throw "Invalid or duplicate locale entry ($locale): $line"
        }
    }
    $keys | Sort-Object
}
$en = @(Get-LocaleKeys 'en')
$zh = @(Get-LocaleKeys 'zh')
if (!$en.Count -or (Compare-Object $en $zh -CaseSensitive)) { throw 'English and Chinese locale keys differ.' }

$required = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $paths) {
    $binary = [DseCecil.ModuleDefinition]::ReadModule($path)
    try {
        if ($binary.Assembly.Name.Name -eq 'ShopAssist' -and
            $binary.Assembly.Name.Version.ToString(3) -ne $manifest.ModVersion) { throw 'DLL and manifest versions differ.' }
        foreach ($type in $binary.GetTypes()) {
            foreach ($method in $type.Methods) {
                if (!$method.HasBody) { continue }
                foreach ($instruction in $method.Body.Instructions) {
                    if ($instruction.OpCode.Code -ne 'Ldstr') { continue }
                    $next = $instruction.Next
                    if ($next -and $next.OpCode.Code -eq 'Call' -and $next.Operand.Name -eq 'Text' -and
                        $next.Operand.DeclaringType.FullName -eq 'ShopAssist.ShopSettings') {
                        [void]$required.Add([string]$instruction.Operand)
                    }
                }
            }
        }
    } finally { $binary.Dispose() }
}
$absent = @($required | Where-Object { $_ -cnotin $en })
if ($absent.Count) { throw "Localized UI text is missing: $($absent -join ', ')" }

# Awake normally populates this set from the game's Managed directory in Unity.
# The offline scanner runs in this PowerShell process without a Unity scene.
$known = [ModLoader].GetField('gameAssemblyNames', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
foreach ($file in Get-ChildItem -File -LiteralPath $managed -Filter '*.dll') { [void]$known.Add($file.BaseName) }
foreach ($local in $true, $false) {
    $result = [ModScanner]::Scan($paths.ToArray(), $names.ToArray(), $package, $local)
    $missing = @($result.Capabilities | Where-Object { $_ -notin $manifest.Capabilities })
    if ($result.Error -or !$result.MethodsScanned -or $result.HardFlags.Count -or $missing.Count) {
        $result.HardFlags | Format-List | Out-Host
        $result.Findings | Format-List | Out-Host
        throw "Scan failed: error=$($result.Error) missing=$($missing -join ', ')"
    }
    Write-Output "Scan $($manifest.ID): local=$local methods=$($result.MethodsScanned) detected=[$($result.Capabilities -join ', ')] hardFlags=0"
}
