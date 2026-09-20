[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Mini Metro',
    [string]$PlayerLog,
    [switch]$StrictLog
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
$python = if ($pythonCommand) { $pythonCommand.Source } else {
    $visionPython = Join-Path $projectRoot 'research\vision\.venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $visionPython -PathType Leaf) {
        $visionPython
    } else {
        $localPrograms = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Python'
        Get-ChildItem -LiteralPath $localPrograms -Filter python.exe -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $python) {
    throw 'Python 3 was not found. It is required only for the offline log analyzer tests.'
}

& (Join-Path $projectRoot 'build.ps1') -GameDir $GameDir
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$patcher = Join-Path $projectRoot 'dist\MiniMetroExtremePlannerPatcher.exe'
$stagedAssembly = Join-Path $projectRoot 'staging\Assembly-CSharp.dll'
& $patcher verify $stagedAssembly
if ($LASTEXITCODE -ne 0) { throw "Patch verification failed with exit code $LASTEXITCODE" }

Push-Location (Join-Path $projectRoot 'tools')
try {
    & $python -m unittest -v test_analyze_player_log.py
    if ($LASTEXITCODE -ne 0) { throw "Tool tests failed with exit code $LASTEXITCODE" }

    if ($PlayerLog) {
        $arguments = @('.\analyze_player_log.py', $PlayerLog)
        if ($StrictLog) { $arguments += '--strict' }
        & $python @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Player log regression analysis failed with exit code $LASTEXITCODE"
        }
    }
}
finally {
    Pop-Location
}

Write-Host 'Project verification complete.'
