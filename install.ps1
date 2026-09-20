[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Mini Metro'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$managedDir = Join-Path $GameDir 'MiniMetro_Data\Managed'
$sourceAssembly = Join-Path $projectRoot 'staging\Assembly-CSharp.dll'
$sourcePlugin = Join-Path $projectRoot 'dist\MiniMetroExtremePlanner.dll'
$targetAssembly = Join-Path $managedDir 'Assembly-CSharp.dll'
$targetPlugin = Join-Path $managedDir 'MiniMetroExtremePlanner.dll'
$backupDir = Join-Path $projectRoot 'backup'
$backupAssembly = Join-Path $backupDir 'Assembly-CSharp.original.dll'
$manifest = Join-Path $backupDir 'install-manifest.txt'
$buildManifest = Join-Path $projectRoot 'staging\build-manifest.txt'

function Read-KeyValueFile([string]$Path) {
    $values = @{}
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $Path) {
            if ($line -match '^([^=]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
        }
    }
    return $values
}

foreach ($path in @($sourceAssembly, $sourcePlugin, $targetAssembly, $buildManifest)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file not found: $path" }
}
if (Get-Process -Name 'MiniMetro' -ErrorAction SilentlyContinue) {
    throw 'Mini Metro is running. Close the game before installing.'
}

New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$build = Read-KeyValueFile $buildManifest
if (-not $build.ContainsKey('BaseSHA256')) { throw 'Build manifest does not contain BaseSHA256. Run build.ps1 again.' }
$previousInstall = Read-KeyValueFile $manifest
$targetHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $targetAssembly).Hash
$baseHash = $build['BaseSHA256']
$replacingPreviousInstall = $previousInstall.ContainsKey('PatchedSHA256') `
    -and $targetHash -eq $previousInstall['PatchedSHA256']

if ($targetHash -eq $baseHash) {
    if (Test-Path -LiteralPath $backupAssembly -PathType Leaf) {
        $existingBackupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backupAssembly).Hash
        if ($existingBackupHash -ne $baseHash) {
            $archivedBackup = Join-Path $backupDir ("Assembly-CSharp.original.{0}.dll" -f $existingBackupHash.Substring(0, 12))
            if (-not (Test-Path -LiteralPath $archivedBackup -PathType Leaf)) {
                Copy-Item -LiteralPath $backupAssembly -Destination $archivedBackup
            }
        }
    }
    Copy-Item -LiteralPath $targetAssembly -Destination $backupAssembly -Force
} elseif ($replacingPreviousInstall) {
    if (-not (Test-Path -LiteralPath $backupAssembly -PathType Leaf)) {
        throw 'The previous planner is installed, but its original game assembly backup is missing.'
    }
    $backupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backupAssembly).Hash
    if ($backupHash -ne $baseHash) {
        throw 'The staged build was not produced from the original assembly paired with the installed planner.'
    }
} else {
    throw 'The game assembly changed after the staged build. Run build.ps1 again before installing.'
}

Copy-Item -LiteralPath $sourcePlugin -Destination $targetPlugin -Force
Copy-Item -LiteralPath $sourceAssembly -Destination $targetAssembly -Force
@(
    "InstalledAt=$([DateTime]::Now.ToString('o'))"
    "GameDir=$GameDir"
    "OriginalSHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $backupAssembly).Hash)"
    "PluginSHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePlugin).Hash)"
    "PatchedSHA256=$((Get-FileHash -Algorithm SHA256 -LiteralPath $sourceAssembly).Hash)"
) | Set-Content -LiteralPath $manifest -Encoding UTF8
Write-Host 'Mini Metro Extreme Auto Planner installed.'
