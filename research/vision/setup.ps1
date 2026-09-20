[CmdletBinding()]
param(
    [string]$Python = 'python',
    [switch]$TrainedModels
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$venv = Join-Path $root '.venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    & $Python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create visual research virtual environment.' }
}
$requirements = if ($TrainedModels) { 'requirements-trained.txt' } else { 'requirements.txt' }
& $venvPython -m pip install --disable-pip-version-check -r (Join-Path $root $requirements)
if ($LASTEXITCODE -ne 0) { throw 'Failed to install visual research dependencies.' }
Write-Host "Vision environment ready: $venvPython ($requirements)"
