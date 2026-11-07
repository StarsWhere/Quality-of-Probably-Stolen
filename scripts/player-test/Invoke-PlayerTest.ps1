[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Next', 'All', 'Release', 'Cleanup')][string]$Action = 'Next',
    [ValidateSet('activate', 'settings', 'persist', 'calendar', 'calendar-reload', 'batch-a', 'batch-a-reload', 'inspection-tree')][string]$Phase,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9]{0,63}Scenario$')][string]$Scenario,
    [string]$GameDir = $env:PROBABLY_STOLEN_GAME_DIR,
    [string]$ModDirectory,
    [string]$ProbeAssembly,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,63}$')][string]$Profile = 'player-test',
    [ValidateRange(640, 7680)][int]$ScreenWidth = 1024,
    [ValidateRange(480, 4320)][int]$ScreenHeight = 768,
    [switch]$Render
)

$ErrorActionPreference = 'Stop'
if (!$GameDir) { $GameDir = 'D:\SteamLibrary\steamapps\common\Probably Stolen' }
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$qa = Join-Path $workspace ('.tmp/' + $Profile)
$player = Join-Path $qa 'player'
$data = Join-Path $qa 'data'
$logs = Join-Path $qa 'log'
$managed = Join-Path $player 'Probably Stolen_Data/Managed'
$identityFile = Join-Path $player 'qa-identity.txt'
$phaseFile = Join-Path $data 'qa-phase.txt'
$marker = Join-Path $player 'qa-owned.txt'
$integrity = Join-Path $qa 'qa-integrity.txt'
$sourceFile = Join-Path $player 'qa-package-source.txt'
if ($Action -ne 'Cleanup') {
    $GameDir = (Resolve-Path -LiteralPath $GameDir).Path
    if (-not $ModDirectory) { $ModDirectory = Join-Path $GameDir 'Mods/ShopAssist' }
}

function Assert-NoLinks([string]$Path) {
    $rootItem = Get-Item -LiteralPath $Path
    if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected link: $Path" }
    $links = @(Get-ChildItem -LiteralPath $Path -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count -ne 0) { throw "Unexpected link within $Path" }
}

function Assert-Player {
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'player-test-v1') {
        throw 'The temporary Player is missing its ownership marker.'
    }
    foreach ($path in @((Join-Path $workspace '.tmp'), $qa, $player, $data)) {
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected workspace link: $path" }
    }
    $identity = @(Get-Content -LiteralPath $identityFile)
    if ($identity.Count -ne 3 -or $identity[0] -cnotmatch '^QA[0-9a-f]{19}$' -or $identity[1] -cnotmatch '^P[0-9a-f]{14}$') { throw 'Invalid QA identity.' }
    $expected = Join-Path $env:USERPROFILE ('AppData/LocalLow/' + $identity[0] + '/' + $identity[1])
    if (-not [string]::Equals([IO.Path]::GetFullPath($identity[2]), [IO.Path]::GetFullPath($expected), [StringComparison]::OrdinalIgnoreCase)) { throw 'QA identity path mismatch.' }
    $link = Get-Item -LiteralPath $expected
    if ($link.LinkType -ne 'Junction' -or -not [string]::Equals([IO.Path]::GetFullPath($link.Target), [IO.Path]::GetFullPath($data), [StringComparison]::OrdinalIgnoreCase)) { throw 'QA data junction mismatch.' }
    $gameExe = Join-Path $GameDir 'Probably Stolen.exe'
    $copyExe = Join-Path $player 'Probably Stolen.exe'
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $gameExe).Hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $copyExe).Hash) { throw 'The Player executable differs from the installed original.' }
    if (@(Get-CimInstance Win32_Process -Filter "Name = 'Probably Stolen.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($player, [StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'The QA Player is already running.' }
    return $identity
}

function Get-PreparedInventory {
    $modRoot = Join-Path $player 'Mods'
    $entries = @(Get-ChildItem -LiteralPath $modRoot -Force)
    if ($entries.Count -ne 1 -or -not $entries[0].PSIsContainer -or $entries[0].Name -cne 'ShopAssist') { throw 'QA Player must contain only ShopAssist.' }
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw 'QA package source is missing.' }
    $critical = @(
        'Probably Stolen_Data/globalgamemanagers', 'Probably Stolen_Data/app.info',
        'Probably Stolen_Data/ScriptingAssemblies.json', 'Probably Stolen_Data/RuntimeInitializeOnLoads.json',
        'Probably Stolen_Data/Managed/Assembly-CSharp.dll', 'Probably Stolen_Data/Managed/Assembly-CSharp-firstpass.dll',
        'Probably Stolen_Data/Managed/com.rlabrecque.steamworks.net.dll', 'Probably Stolen_Data/Managed/PlayerProbe.dll',
        'Probably Stolen_Data/Managed/UnityEngine.CoreModule.dll', 'steam_appid.txt'
    )
    $items = foreach ($relative in $critical) {
        $path = Join-Path $player $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Prepared file missing: $relative" }
        $relative.Replace('\', '/') + '|' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $items += 'qa-package-source.txt|' + (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
    foreach ($entry in $entries) {
        foreach ($file in Get-ChildItem -LiteralPath $entry.FullName -Recurse -File | Sort-Object FullName) {
            $relative = $file.FullName.Substring($player.Length + 1).Replace('\', '/')
            $items += $relative + '|' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return @($items | Sort-Object)
}

function Assert-PackageMirror {
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw 'QA package source is missing.' }
    $sourcePath = (Get-Content -LiteralPath $sourceFile -Raw).Trim()
    $source = (Resolve-Path -LiteralPath $sourcePath).Path
    $copy = Join-Path (Join-Path $player 'Mods') 'ShopAssist'
    Assert-NoLinks $source
    Assert-NoLinks $copy
    $sourceFiles = @(Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object { $_.FullName.Substring($source.Length + 1) } | Sort-Object)
    $copyFiles = @(Get-ChildItem -LiteralPath $copy -Recurse -File | ForEach-Object { $_.FullName.Substring($copy.Length + 1) } | Sort-Object)
    if ($sourceFiles.Count -ne $copyFiles.Count -or (Compare-Object $sourceFiles $copyFiles -CaseSensitive)) { throw "Package file list changed: $source" }
    foreach ($relative in $sourceFiles) {
        if ((Get-FileHash -LiteralPath (Join-Path $source $relative) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $copy $relative) -Algorithm SHA256).Hash) { throw "Package bytes changed: $source/$relative" }
    }
}

function Prepare-Player {
    $gameExe = Join-Path $GameDir 'Probably Stolen.exe'
    if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $ModDirectory 'manifest.xml') -PathType Leaf)) { throw 'Game executable or MOD manifest missing.' }
    Assert-NoLinks $ModDirectory
    $sourcePackage = (Resolve-Path -LiteralPath $ModDirectory).Path
    if (-not (Test-Path -LiteralPath $qa)) { New-Item -ItemType Directory -Path $qa | Out-Null }
    if (-not (Test-Path -LiteralPath $data)) { New-Item -ItemType Directory -Path $data | Out-Null }
    if (-not (Test-Path -LiteralPath $logs)) { New-Item -ItemType Directory -Path $logs | Out-Null }
    if (-not (Test-Path -LiteralPath $player)) {
        $sourceItems = @(Get-ChildItem -LiteralPath $GameDir -Force | Where-Object Name -ne 'Mods')
        foreach ($item in $sourceItems) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected game installation link: $($item.FullName)" }
            if ($item.PSIsContainer) { Assert-NoLinks $item.FullName }
        }
        New-Item -ItemType Directory -Path $player | Out-Null
        foreach ($item in $sourceItems) { Copy-Item -LiteralPath $item.FullName -Destination $player -Recurse }
        [IO.File]::WriteAllText($marker, 'player-test-v1')
        $company = 'QA' + [Guid]::NewGuid().ToString('N').Substring(0, 19)
        $product = 'P' + [Guid]::NewGuid().ToString('N').Substring(0, 14)
        $companyDir = Join-Path $env:USERPROFILE ('AppData/LocalLow/' + $company)
        if (Test-Path -LiteralPath $companyDir) { throw 'Generated QA company directory already exists.' }
        New-Item -ItemType Directory -Path $companyDir | Out-Null
        $alias = Join-Path $companyDir $product
        New-Item -ItemType Junction -Path $alias -Target $data | Out-Null
        [IO.File]::WriteAllText($identityFile, $company + "`n" + $product + "`n" + $alias)
    }
    $null = Assert-Player
    Assert-NoLinks $player
    if ((Test-Path -LiteralPath $integrity -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $player 'Mods'))) {
        $oldInventory = @(Get-Content -LiteralPath $integrity)
        $currentInventory = @(Get-PreparedInventory)
        if ($oldInventory.Count -ne $currentInventory.Count -or (Compare-Object $oldInventory $currentInventory)) { throw 'QA Player changed after preparation; refusing to replace its MODs.' }
    }

    $build = Join-Path $qa 'build'
    $obj = (Join-Path $qa 'buildobj').Replace('\', '/') + '/'
    if ($ProbeAssembly) {
        $probeDll = (Resolve-Path -LiteralPath $ProbeAssembly).Path
        if ([Reflection.AssemblyName]::GetAssemblyName($probeDll).Name -ne 'PlayerProbe') { throw 'Expected a PlayerProbe assembly.' }
    } else {
        & dotnet build (Join-Path $PSScriptRoot 'PlayerProbe/PlayerProbe.csproj') -c Release -o $build "-p:GameDir=$GameDir" "-p:BaseIntermediateOutputPath=$obj" -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'QA driver build failed.' }
        $probeDll = Join-Path $build 'PlayerProbe.dll'
    }
    $sourceData = Join-Path $GameDir 'Probably Stolen_Data'
    $copyData = Join-Path $player 'Probably Stolen_Data'
    foreach ($name in @('globalgamemanagers', 'app.info', 'ScriptingAssemblies.json', 'Managed/Assembly-CSharp.dll', 'Managed/com.rlabrecque.steamworks.net.dll')) {
        Copy-Item -LiteralPath (Join-Path $sourceData $name) -Destination (Join-Path $copyData $name) -Force
    }
    $modRoot = Join-Path $player 'Mods'
    if (-not (Test-Path -LiteralPath $modRoot)) { New-Item -ItemType Directory -Path $modRoot | Out-Null }
    $expectedModRoot = [IO.Path]::GetFullPath((Join-Path $player 'Mods'))
    if (-not [string]::Equals((Resolve-Path -LiteralPath $modRoot).Path, $expectedModRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected QA MOD root.' }
    $targetPackage = [IO.Path]::GetFullPath((Join-Path $modRoot 'ShopAssist'))
    $separator = [IO.Path]::DirectorySeparatorChar
    if ([string]::Equals($sourcePackage, $expectedModRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $sourcePackage.StartsWith($expectedModRoot + $separator, [StringComparison]::OrdinalIgnoreCase) -or
        $expectedModRoot.StartsWith($sourcePackage.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The source MOD and QA MOD root cannot contain one another.'
    }
    $entries = @(Get-ChildItem -LiteralPath $modRoot -Force)
    if ($entries.Count -gt 1 -or ($entries.Count -eq 1 -and (-not $entries[0].PSIsContainer -or $entries[0].Name -cne 'ShopAssist'))) { throw 'Unexpected QA MOD entry.' }
    if ($entries.Count -eq 1) {
        if (-not [string]::Equals((Resolve-Path -LiteralPath $entries[0].FullName).Path, $targetPackage, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected QA MOD path.' }
        Assert-NoLinks $targetPackage
        Remove-Item -LiteralPath $targetPackage -Recurse -Force
    }
    Copy-Item -LiteralPath $sourcePackage -Destination $targetPackage -Recurse
    [IO.File]::WriteAllText($sourceFile, $sourcePackage)
    Assert-PackageMirror
    [IO.File]::WriteAllText((Join-Path $player 'steam_appid.txt'), '4348910')
    & (Join-Path $PSScriptRoot 'InstallBootstrap.ps1') -Root $player -GameDir $GameDir -BootstrapDll $probeDll -Profile $Profile
    [IO.File]::WriteAllLines($integrity, [string[]](Get-PreparedInventory))
    Write-Output "Prepared isolated Player under $player; retained data under $data"
}

function Invoke-Next {
    $identity = Assert-Player
    if (-not (Test-Path -LiteralPath (Join-Path $managed 'PlayerProbe.dll'))) { throw 'Prepare the QA Player first.' }
    Assert-NoLinks $player
    if (-not (Test-Path -LiteralPath $integrity -PathType Leaf)) { throw 'QA integrity manifest missing; prepare the Player again.' }
    $expectedInventory = @(Get-Content -LiteralPath $integrity)
    $actualInventory = @(Get-PreparedInventory)
    if ($expectedInventory.Count -ne $actualInventory.Count -or (Compare-Object $expectedInventory $actualInventory)) { throw 'QA Player changed after preparation; refusing to launch.' }
    Assert-PackageMirror
    foreach ($relative in @('Probably Stolen_Data/Managed/Assembly-CSharp-firstpass.dll', 'Probably Stolen_Data/Managed/UnityEngine.CoreModule.dll', 'Probably Stolen_Data/RuntimeInitializeOnLoads.json')) {
        if ((Get-FileHash -LiteralPath (Join-Path $player $relative) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $GameDir $relative) -Algorithm SHA256).Hash) { throw "Game version changed after preparation: $relative" }
    }
    $phaseName = if (Test-Path -LiteralPath $phaseFile) { (Get-Content -LiteralPath $phaseFile -Raw).Trim() } else { 'activate' }
    if ($phaseName -notin @('activate', 'settings', 'persist', 'calendar', 'calendar-reload', 'batch-a', 'batch-a-reload', 'inspection-tree') -and
        $phaseName -cnotmatch '^scenario-[A-Za-z][A-Za-z0-9]{0,63}Scenario$') { throw "Unknown or finished phase: $phaseName" }
    $runId = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $player 'qa-run-id.txt'), $runId)
    $log = Join-Path $logs ("Player-$phaseName-$runId.log")
    $arguments = if ($Render) { "-screen-width $ScreenWidth -screen-height $ScreenHeight -screen-fullscreen 0" } else { '-batchmode -nographics' }
    $arguments += ' -logFile "' + $log + '"'
    $background = $null
    if ($Render) {
        if (-not ('BackgroundPlayer' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'BackgroundPlayer.cs') }
        $background = [BackgroundPlayer]::Start((Join-Path $player 'Probably Stolen.exe'), $player, $arguments)
        $process = $background.Process
    } else {
        $process = Start-Process -FilePath (Join-Path $player 'Probably Stolen.exe') -WorkingDirectory $player -ArgumentList $arguments -PassThru -WindowStyle Hidden
    }
    try {
        Write-Output "QA phase=$phaseName PID=$($process.Id) privateDesktop=$([bool]$background) log=$log"
        $expires = [DateTime]::UtcNow.AddSeconds(270)
        while (-not $process.WaitForExit(5000)) {
            if ([DateTime]::UtcNow -ge $expires) { $process.Kill(); $process.WaitForExit(); throw "QA phase $phaseName timed out." }
        }
    } finally {
        if ($null -ne $background) { $background.Dispose() }
    }
    $exitCode = if ($null -ne $background) { $background.ExitCode } else { $process.ExitCode }
    $status = Join-Path $data ("qa-status-$runId.txt")
    $proof = Join-Path $data 'guard-proof.txt'
    if (-not (Test-Path -LiteralPath $proof) -or (Get-Content -LiteralPath $proof -Tail 1).Trim() -ne $runId) { throw "QA identity guard did not complete; exit=$exitCode, log=$log" }
    $errors = Join-Path $data ("qa-unity-errors-$runId.txt")
    if (-not (Test-Path -LiteralPath $errors -PathType Leaf)) { throw "QA Unity error capture was not installed; log=$log" }
    $afterInventory = @(Get-PreparedInventory)
    if ($expectedInventory.Count -ne $afterInventory.Count -or (Compare-Object $expectedInventory $afterInventory)) { throw "QA Player changed during launch; log=$log" }
    Assert-PackageMirror
    if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $status) -or (Get-Content -LiteralPath $status -Raw).Trim() -ne 'PASS') { throw "QA phase $phaseName failed; exit=$exitCode, log=$log" }
    if ((Get-Item -LiteralPath $errors).Length -ne 0) { throw "QA phase $phaseName emitted Unity errors; errors=$errors, log=$log" }
    Write-Output "PASS phase=$phaseName; data=$data"
}

function Remove-QaFiles {
    if (-not (Test-Path -LiteralPath $qa)) { Write-Output 'QA files are already absent.'; return }
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'player-test-v1') { throw 'QA ownership marker missing.' }
    $expectedQa = [IO.Path]::GetFullPath((Join-Path $workspace ('.tmp/' + $Profile)))
    if (-not [string]::Equals((Resolve-Path -LiteralPath $qa).Path, $expectedQa, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected QA cleanup path.' }
    foreach ($path in @((Join-Path $workspace '.tmp'), $qa, $player, $data)) {
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected workspace link: $path" }
    }
    $identity = @(Get-Content -LiteralPath $identityFile)
    if ($identity.Count -ne 3 -or $identity[0] -cnotmatch '^QA[0-9a-f]{19}$' -or $identity[1] -cnotmatch '^P[0-9a-f]{14}$') { throw 'Invalid QA identity.' }
    $companyDir = Join-Path $env:USERPROFILE ('AppData/LocalLow/' + $identity[0])
    $alias = Join-Path $companyDir $identity[1]
    if (-not [string]::Equals([IO.Path]::GetFullPath($identity[2]), [IO.Path]::GetFullPath($alias), [StringComparison]::OrdinalIgnoreCase)) { throw 'QA alias path mismatch.' }
    if (Test-Path -LiteralPath $companyDir) {
        if ((Get-Item -LiteralPath $companyDir).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'QA company folder is a link.' }
    }
    if (Test-Path -LiteralPath $alias) {
        $link = Get-Item -LiteralPath $alias
        if ($link.LinkType -ne 'Junction' -or -not [string]::Equals([IO.Path]::GetFullPath($link.Target), [IO.Path]::GetFullPath($data), [StringComparison]::OrdinalIgnoreCase)) { throw 'QA data junction mismatch.' }
    }
    if (@(Get-CimInstance Win32_Process -Filter "Name = 'Probably Stolen.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($player, [StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'The QA Player is still running.' }
    Assert-NoLinks $qa
    $registryCompany = 'HKCU:\Software\' + $identity[0]
    $registryProduct = $registryCompany + '\' + $identity[1]
    if (Test-Path -LiteralPath $registryProduct) { Remove-Item -LiteralPath $registryProduct -Recurse }
    if (Test-Path -LiteralPath $registryCompany) {
        $key = Get-Item -LiteralPath $registryCompany
        if ($key.SubKeyCount -eq 0 -and $key.ValueCount -eq 0) { Remove-Item -LiteralPath $registryCompany }
    }
    if (Test-Path -LiteralPath $alias) { Remove-Item -LiteralPath $alias }
    if ((Test-Path -LiteralPath $companyDir) -and @(Get-ChildItem -LiteralPath $companyDir -Force).Count -eq 0) { Remove-Item -LiteralPath $companyDir }
    Remove-Item -LiteralPath $qa -Recurse -Force
    Write-Output 'Removed only the isolated QA identity and player-test files.'
}

if ($Scenario -and ($Phase -or $Action -ne 'Next')) { throw 'Scenario is only valid with Next and cannot be combined with Phase.' }
if ($Action -eq 'Release' -and (!$Render -or $Phase)) { throw 'The release suite requires -Render and starts with its own activation phase.' }
if ($Action -eq 'Prepare') { Prepare-Player; return }
if ($Action -eq 'Cleanup') { Remove-QaFiles; return }
$null = Assert-Player
if ($Phase) { [IO.File]::WriteAllText($phaseFile, $Phase) }
if ($Scenario) { [IO.File]::WriteAllText($phaseFile, 'scenario-' + $Scenario) }
if ($Action -eq 'Next') { Invoke-Next; return }
if (-not $Phase) { [IO.File]::WriteAllText($phaseFile, 'activate') }
$morningCompleted = $false
for ($i = 0; $i -lt 6; $i++) {
    Invoke-Next
    if ((Get-Content -LiteralPath $phaseFile -Raw).Trim() -eq 'calendar-complete') { $morningCompleted = $true; break }
}
if (!$morningCompleted) { throw 'QA sequence did not complete within six Player launches.' }
if ($Action -ne 'Release') { return }
foreach ($next in 'batch-a','batch-a-reload') { [IO.File]::WriteAllText($phaseFile,$next); Invoke-Next }
# Keep each write/read pair adjacent. A failed producer must never be followed by a stale reload check.
$scenarios = @(
    'FastPlaybackInputScenario','FastPlaybackDialogueScenario','FastPlaybackMotionScenario','FastPlaybackTransitionsScenario','FastPlaybackTransitionsStoryScenario','FastPlaybackTransitionsFlowScenario','FastPlaybackTransitionsReloadScenario',
    'DoorScenario','DoorLayerScenario','DoorScaleScenario','DoorDeadlineScenario','DailyDeadlineScenario',
    'ContextToolsScenario','PanelToolsScenario','ScannerPresentationScenario','ScannerScaleScenario','ScannerLayoutScenario','ScannerUxScenario','ScannerPointerScenario','ItemLabelsScenario',
    'DailyMarkerSwitchScenario','StorageAnchorScenario','ModuleGridScenario','ModuleBayShapeScenario','ModuleScaleScenario','ModuleScaleEnglishScenario','ModuleSearchScenario',
    'GroupGridScenario','DailyGroupInputScenario','DailyGroupSwitchScenario','MachineOverviewScenario','MachineAttentionScenario',
    'FeatureTierDefaultsScenario','SettingsPresentationScenario','SettingsFinderScenario','SharedUxScenario','PinSettingsScenario','ContextWindowStabilityScenario','PickerLifecycleScenario','FeedbackScenario','FeedbackReloadScenario','AttentionLocationScenario','TodoRefreshScenario',
    'CustomerScenario','CustomerQuoteScenario','CustomerCounterDropScenario','StockSearchScenario','StockSearchEnglishScenario','StockSearchDragInputScenario','ContextShortcutScenario','MachinesScenario','MachineLifecycleScenario',
    'ScannerTreatmentScenario','ScannerFilterMatrixScenario','ScannerMachineTreatmentScenario',
    'OrderScenario','RequestColdScenario','RequestColdReloadScenario','ReservationMoveScenario','ReservationCorruptScenario','ReservationSaveFailureScenario','RequestSafetyScenario',
    'DesktopScenario','DailyDesktopSaveScenario','DailyDesktopReloadScenario',
    'TradeScenario','DailyReceiptScenario','ReceiptSoundScenario','ReceiptBurstScenario','LedgerMarginScenario','LedgerDaysScenario','LedgerDaysReloadScenario',
    'WholesaleScenario','WholesaleReloadScenario','YeastOriginScenario','YeastColdBrewScenario','PeekScenario','DailyPeekInputScenario',
    'AppraisalWorksheetScenario','ReceiptScenario','FieldScenario','FieldValueScenario','LabelBatchPagesScenario','CargoDestinationScenario','PlainLabelScenario','LabelOverwriteScenario',
    'DisplaySourceScenario','DisplayCapacityScenario','DisplaySplitScenario','DisplaySplitRecoveryScenario','PackingPriorityScenario',
    'PackingRecoveryScenario','PackingPocketRecoveryScenario','WaterBasketScenario','WaterFilterPauseScenario','WaterSafetyScenario',
    'BasketBudgetScenario','BasketDifferentQuotesScenario',
    'OrganizerScenario','ArrangeInputScenario','AutoArrangeInputScenario','GameBuildGuardScenario','RoutingScenario','RoutingVetoScenario','RoutingBoundaryScenario','CounterRoutingScenario','WholeStackRestartScenario','WholeStackRestartReloadScenario',
    'LayoutScenario','LayoutReloadScenario','LayoutReflowScenario','MoistureLayoutScenario',
    'ReplantScenario','ReplantReloadScenario','ReplantRecoveryScenario','ReplantRecoveryReloadScenario','FeedScenario','FeedReloadScenario','FeedCapacityScenario','FeedFullRecoveryScenario',
    'MaintenanceScenario','MaintenanceReloadScenario','MaintenanceReservationScenario','MaintenanceCopyScenario',
    'OccupiedSelectionScenario','EvaporatorLayoutScenario','PrinterLayoutScenario','FurnaceLayoutScenario',
    'EquipmentOverviewScenario','PrinterMaintenanceScenario','FilterOwnerScenario','RasMaintenanceScenario',
    'FilterGradeScenario','MachineBindingScenario','MachineBindingReloadScenario','ModulePagesScenario','ShortcutOwnershipScenario','RepeatedPurchaseScenario',
    'YeastVetoScenario','YeastPageScenario','YeastFallbackScenario','AllFeaturesScenario'
)
foreach ($case in $scenarios) {
    [IO.File]::WriteAllText($phaseFile, 'scenario-' + $case)
    Invoke-Next
}
Write-Output "PASS release suite: activation, settings, calendar, two-save checklist and $($scenarios.Count) rendered scenarios."
