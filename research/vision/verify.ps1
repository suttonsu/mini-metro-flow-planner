[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Archive
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$python = Join-Path $root '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    throw 'Vision environment is missing. Run setup.ps1 first.'
}
if (-not (Test-Path -LiteralPath $Archive -PathType Leaf)) {
    throw "Legacy Mini Metro archive not found: $Archive"
}

$env:PYTHONPATH = $root
$env:MINI_METRO_LEGACY_ARCHIVE = $Archive
& $python -m py_compile (Get-ChildItem -LiteralPath (Join-Path $root 'mini_metro_vision') -Filter '*.py' | Select-Object -ExpandProperty FullName)
if ($LASTEXITCODE -ne 0) { throw 'Vision Python syntax validation failed.' }
& $python -m unittest -v (Join-Path $root 'tests\test_core.py')
if ($LASTEXITCODE -ne 0) { throw 'Vision unit tests failed.' }
& $python -m mini_metro_vision --archive $Archive train-context
if ($LASTEXITCODE -ne 0) { throw 'Context training/evaluation failed.' }
& $python -m mini_metro_vision --archive $Archive evaluate `
    --minimum-context-accuracy 0.95 `
    --minimum-station-f1 0.95
if ($LASTEXITCODE -ne 0) { throw 'Vision quality gate failed.' }
Write-Host 'Vision research verification complete.'
