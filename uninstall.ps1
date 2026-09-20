[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Mini Metro'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$managedDir = Join-Path $GameDir 'MiniMetro_Data\Managed'
$backupAssembly = Join-Path $projectRoot 'backup\Assembly-CSharp.original.dll'
$targetAssembly = Join-Path $managedDir 'Assembly-CSharp.dll'
$targetPlugin = Join-Path $managedDir 'MiniMetroExtremePlanner.dll'

if (-not (Test-Path -LiteralPath $backupAssembly -PathType Leaf)) {
    throw "Original assembly backup not found: $backupAssembly"
}
if (Get-Process -Name 'MiniMetro' -ErrorAction SilentlyContinue) {
    throw 'Mini Metro is running. Close the game before uninstalling.'
}

Copy-Item -LiteralPath $backupAssembly -Destination $targetAssembly -Force
if (Test-Path -LiteralPath $targetPlugin -PathType Leaf) {
    Remove-Item -LiteralPath $targetPlugin -Force
}
Write-Host 'Mini Metro Extreme Auto Planner removed; original Assembly-CSharp.dll restored.'
