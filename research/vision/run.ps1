[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = 'Stop'
$python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    throw 'Vision environment is missing. Run setup.ps1 first.'
}
$env:PYTHONPATH = $PSScriptRoot
& $python -m mini_metro_vision @Arguments
exit $LASTEXITCODE
