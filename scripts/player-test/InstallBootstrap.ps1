param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$BootstrapDll
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Root).Path
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $workspace '.tmp/player-test/player'))
if (-not [string]::Equals($root, $expectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath (Join-Path $root 'qa-owned.txt') -PathType Leaf) -or
    ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Bootstrap installer may only modify the owned player-test copy.'
}
foreach ($path in @((Join-Path $workspace '.tmp'), (Join-Path $workspace '.tmp/player-test'), $root,
    (Join-Path $root 'Probably Stolen_Data'), (Join-Path $root 'Probably Stolen_Data/Managed'))) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected link in QA path: $path" }
}
$managed = Join-Path $root 'Probably Stolen_Data/Managed'
$originalManaged = Join-Path $GameDir 'Probably Stolen_Data/Managed'
$identity = Get-Content -LiteralPath (Join-Path $root 'qa-identity.txt')
if ($identity.Count -ne 3 -or $identity[0].Length -ne 21 -or $identity[1].Length -ne 15) { throw 'Unexpected identity' }

$assetPath = Join-Path $root 'Probably Stolen_Data/globalgamemanagers'
$bytes = [IO.File]::ReadAllBytes($assetPath)
$ascii = [Text.Encoding]::ASCII
$old = $ascii.GetBytes('Questing Goose Studio' + "`0`0`0" + [char]15 + "`0`0`0" + 'Probably Stolen')
$new = $ascii.GetBytes($identity[0] + "`0`0`0" + [char]15 + "`0`0`0" + $identity[1])
$positions = @()
for ($i=0; $i -le $bytes.Length-$old.Length; $i++) {
    if ($bytes[$i] -ne $old[0]) { continue }
    $match = $true
    for ($j=1; $j -lt $old.Length; $j++) { if ($bytes[$i+$j] -ne $old[$j]) { $match = $false; break } }
    if ($match) { $positions += $i }
}
if ($positions.Count -ne 1 -or $old.Length -ne $new.Length) { throw 'PlayerSettings identity pattern is not unique' }
[Array]::Copy($new, 0, $bytes, $positions[0], $new.Length)
[IO.File]::WriteAllBytes($assetPath, $bytes)
[IO.File]::WriteAllText((Join-Path $root 'Probably Stolen_Data/app.info'), ($identity[0] + "`n" + $identity[1] + "`n"))

Add-Type -LiteralPath (Join-Path $originalManaged 'Unity.Burst.Cecil.dll')
Copy-Item -LiteralPath $BootstrapDll -Destination (Join-Path $managed 'PlayerProbe.dll') -Force
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new(); $resolver.AddSearchDirectory($managed)
$reader = [Mono.Cecil.ReaderParameters]::new(); $reader.InMemory = $true; $reader.AssemblyResolver = $resolver
$gamePath = Join-Path $managed 'Assembly-CSharp.dll'
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gamePath, $reader)
$probe = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'PlayerProbe.dll'), $reader)
try {
    $steam = $game.MainModule.Types | Where-Object FullName -eq 'SteamManager'
    $awake = $steam.Methods | Where-Object Name -eq 'Awake'
    $instance = $steam.Fields | Where-Object Name -eq 's_instance'
    if (@($steam).Count -ne 1 -or @($awake).Count -ne 1 -or @($instance).Count -ne 1) { throw 'Unexpected SteamManager shape' }
    $awake.Body.ExceptionHandlers.Clear(); $awake.Body.Variables.Clear(); $awake.Body.Instructions.Clear()
    $il = $awake.Body.GetILProcessor()
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Stsfld, [Mono.Cecil.FieldReference]$instance))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $bootstrap = $game.MainModule.Types | Where-Object FullName -eq 'CultureBootstrap'
    $beforeScene = $bootstrap.Methods | Where-Object Name -eq 'PinInvariantCulture'
    $probeMethod = ($probe.MainModule.Types | Where-Object FullName -eq 'PlayerProbe').Methods | Where-Object Name -eq 'Start'
    if (@($beforeScene).Count -ne 1 -or @($probeMethod).Count -ne 1) { throw 'Unexpected runtime initializer or bootstrap' }
    $il = $beforeScene.Body.GetILProcessor()
    $il.InsertBefore($beforeScene.Body.Instructions[0], $il.Create([Mono.Cecil.Cil.OpCodes]::Call, [Mono.Cecil.MethodReference]$game.MainModule.ImportReference($probeMethod)))
    $loader = $game.MainModule.Types | Where-Object FullName -eq 'ModLoader'
    $loaderStart = $loader.Methods | Where-Object Name -eq 'Start'
    $begin = ($probe.MainModule.Types | Where-Object FullName -eq 'PlayerProbe').Methods | Where-Object Name -eq 'Begin'
    if (@($loaderStart).Count -ne 1 -or @($begin).Count -ne 1) { throw 'Unexpected loader start or QA driver' }
    $il = $loaderStart.Body.GetILProcessor()
    $il.InsertBefore($loaderStart.Body.Instructions[0], $il.Create([Mono.Cecil.Cil.OpCodes]::Call, [Mono.Cecil.MethodReference]$game.MainModule.ImportReference($begin)))
    $game.Write($gamePath + '.new')
} finally { $game.Dispose(); $probe.Dispose() }
Move-Item -LiteralPath ($gamePath + '.new') -Destination $gamePath -Force

$steamPath = Join-Path $managed 'com.rlabrecque.steamworks.net.dll'
$steamAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($steamPath, $reader)
try {
    $refs = @($steamAssembly.MainModule.ModuleReferences | Where-Object { $_.Name -in @('steam_api64', 'sdkencryptedappticket64') })
    if ($refs.Count -ne 2) { throw 'Unexpected Steam native references' }
    foreach ($ref in $refs) { $ref.Name = 'disabled_steam_in_isolated_player_' + [Guid]::NewGuid().ToString('N') }
    $steamAssembly.Write($steamPath + '.new')
} finally { $steamAssembly.Dispose() }
Move-Item -LiteralPath ($steamPath + '.new') -Destination $steamPath -Force

$assemblyList = Join-Path $root 'Probably Stolen_Data/ScriptingAssemblies.json'
$json = Get-Content -LiteralPath $assemblyList -Raw | ConvertFrom-Json
$json.names += 'PlayerProbe.dll'; $json.types += 16
$json | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath $assemblyList -Encoding utf8NoBOM
Write-Output "Prepared isolated Player identity $($identity[0])/$($identity[1])"
