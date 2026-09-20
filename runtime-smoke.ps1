[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Mini Metro',
    [int]$TimeoutSeconds = 45,
    [int]$MinimumActions = 1,
    [switch]$VisionObserver,
    [string]$VisionArchive = $env:MINI_METRO_VISION_ARCHIVE
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$managedDir = Join-Path $GameDir 'MiniMetro_Data\Managed'
$gameExe = Join-Path $GameDir 'MiniMetro.exe'
$targetAssembly = Join-Path $managedDir 'Assembly-CSharp.dll'
$targetPlugin = Join-Path $managedDir 'MiniMetroExtremePlanner.dll'
$productionAssembly = Join-Path $projectRoot 'staging\Assembly-CSharp.dll'
$productionPlugin = Join-Path $projectRoot 'dist\MiniMetroExtremePlanner.dll'
$patcher = Join-Path $projectRoot 'dist\MiniMetroExtremePlannerPatcher.exe'
$smokeDir = Join-Path $projectRoot 'staging\smoke'
$smokeAssembly = Join-Path $smokeDir 'Assembly-CSharp.dll'
$capturedLog = Join-Path $projectRoot 'runtime-smoke-current.log'
$visionRoot = Join-Path $projectRoot 'research\vision'
$visionPython = Join-Path $visionRoot '.venv\Scripts\python.exe'
$visionOutput = Join-Path $visionRoot 'artifacts\current-smoke-observations.jsonl'
$visionFrames = Join-Path $visionRoot 'artifacts\current-smoke-frames'
$visionRawFrames = Join-Path $visionRoot 'artifacts\current-smoke-raw'
$playerLog = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) `
    '..\LocalLow\Dinosaur Polo Club\Mini Metro\Player.log'
$playerLog = [IO.Path]::GetFullPath($playerLog)
$visionControlState = Join-Path (Split-Path $playerLog -Parent) 'vision-control-state.txt'

foreach ($path in @(
    $gameExe,
    $targetAssembly,
    $productionAssembly,
    $productionPlugin,
    $patcher
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required smoke-test file not found: $path"
    }
}
if (Get-Process -Name 'MiniMetro' -ErrorAction SilentlyContinue) {
    throw 'Mini Metro is already running. Close it before the runtime smoke test.'
}
if ($MinimumActions -lt 1) { throw 'MinimumActions must be at least 1.' }

$productionAssemblyHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $productionAssembly).Hash
$productionPluginHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $productionPlugin).Hash
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $targetAssembly).Hash -ne $productionAssemblyHash) {
    throw 'Installed Assembly-CSharp.dll does not match the verified production build.'
}
if (-not (Test-Path -LiteralPath $targetPlugin -PathType Leaf) -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $targetPlugin).Hash -ne $productionPluginHash) {
    throw 'Installed planner plugin does not match the verified production build.'
}

New-Item -ItemType Directory -Path $smokeDir -Force | Out-Null
& $patcher smoke $productionAssembly $smokeAssembly $managedDir
if ($LASTEXITCODE -ne 0) { throw "Smoke assembly creation failed with exit code $LASTEXITCODE" }

$gameProcess = $null
$passed = $false
$visionCompleted = $false
$visionGateProbed = -not $VisionObserver
try {
    if ($VisionObserver) {
        foreach ($path in @($visionPython, $VisionArchive)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Vision smoke dependency not found: $path"
            }
        }
        $env:PYTHONPATH = $visionRoot
        & $visionPython -m mini_metro_vision `
            --archive $VisionArchive `
            publish-probe `
            --control-state $visionControlState `
            --station-count 99 `
            --native-station-count 3
        if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the visual mismatch probe.' }
    }
    Copy-Item -LiteralPath $smokeAssembly -Destination $targetAssembly -Force
    $gameProcess = Start-Process -FilePath $gameExe -WorkingDirectory $GameDir -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        if ($gameProcess.HasExited) {
            throw "Mini Metro exited during smoke test with code $($gameProcess.ExitCode)."
        }
        if (-not (Test-Path -LiteralPath $playerLog -PathType Leaf)) { continue }
        $logText = Get-Content -LiteralPath $playerLog -Raw -ErrorAction SilentlyContinue
        $hasSession = $logText -match '\[Auto Planner\]\[Session\] START .*city=london mode=CLASSIC'
        $actionCount = ([regex]::Matches($logText, '\[Auto Planner\]\[Action\]')).Count
        $hasPlannerException = $logText -match '\[Extreme Auto Planner\]'
        if ($hasPlannerException) {
            throw 'Planner exception detected in Player.log during smoke test.'
        }
        if ($VisionObserver -and $hasSession -and -not $visionGateProbed `
            -and $logText -match '\[Auto Planner\]\[VisionControl\] mode=HoldStationMismatch') {
            if ($actionCount -ne 0) {
                throw 'Planner acted before the enforced visual mismatch gate was observed.'
            }
            $visionGateProbed = $true
        }
        if ($VisionObserver -and $visionGateProbed -and -not $visionCompleted) {
            & $visionPython -m mini_metro_vision `
                --archive $VisionArchive `
                control `
                --seconds 8 `
                --interval 1 `
                --player-log $playerLog `
                --output $visionOutput `
                --annotated-dir $visionFrames `
                --raw-dir $visionRawFrames `
                --control-state $visionControlState `
                --enforce-control
            if ($LASTEXITCODE -ne 0) { throw 'Vision control sidecar failed during runtime smoke test.' }
            $visionCompleted = $true
            $logText = Get-Content -LiteralPath $playerLog -Raw -ErrorAction SilentlyContinue
            $actionCount = ([regex]::Matches($logText, '\[Auto Planner\]\[Action\]')).Count
        }
        $visionIntegrated = -not $VisionObserver -or (
            $visionCompleted `
                -and $logText -match '\[Auto Planner\]\[VisionControl\] mode=Aligned' `
                -and $logText -match 'vision=Aligned:'
        )
        if ($VisionObserver -and $visionCompleted) {
            $firstAligned = $logText.IndexOf('[Auto Planner][VisionControl] mode=Aligned')
            $firstAction = $logText.IndexOf('[Auto Planner][Action]')
            if ($firstAction -ge 0 -and ($firstAligned -lt 0 -or $firstAction -lt $firstAligned)) {
                throw 'Planner acted before the trained visual sidecar reached Aligned.'
            }
        }
        if ($hasSession -and $actionCount -ge $MinimumActions -and $visionIntegrated) {
            $passed = $true
            break
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    if (-not $passed) {
        throw "Runtime smoke test timed out after $TimeoutSeconds seconds before reaching $MinimumActions actions."
    }
    if ($VisionObserver) {
        $observations = @(Get-Content -LiteralPath $visionOutput | ForEach-Object { $_ | ConvertFrom-Json })
        $usable = @($observations | Where-Object {
            $_.context.label -eq 'game_play' `
                -and $_.vision_station_count -gt 0 `
                -and $_.native.stations -gt 0 `
                -and [Math]::Abs([int]$_.station_count_delta) -le 2
        })
        if ($usable.Count -eq 0) {
            throw 'Vision smoke produced no game_play observation within two stations of native telemetry.'
        }
        if (-not ($logText -match '\[Auto Planner\]\[VisionControl\] mode=Aligned')) {
            throw 'C# planner never consumed an aligned enforced vision-control state.'
        }
    }
    Copy-Item -LiteralPath $playerLog -Destination $capturedLog -Force
}
finally {
    if ($gameProcess -and -not $gameProcess.HasExited) {
        Stop-Process -Id $gameProcess.Id -Force
        $gameProcess.WaitForExit()
    }
    Copy-Item -LiteralPath $productionAssembly -Destination $targetAssembly -Force
}

if ((Get-FileHash -Algorithm SHA256 -LiteralPath $targetAssembly).Hash -ne $productionAssemblyHash) {
    throw 'Production game assembly restoration failed after smoke test.'
}
& $patcher verify $targetAssembly
if ($LASTEXITCODE -ne 0) { throw 'Restored production assembly failed verification.' }
Write-Host "Runtime smoke test passed with at least $MinimumActions actions. Captured log: $capturedLog"
if ($VisionObserver) { Write-Host "Vision observations: $visionOutput" }
