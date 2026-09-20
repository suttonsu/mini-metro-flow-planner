[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Mini Metro',
    [string]$CecilPath = $env:MONO_CECIL_PATH
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$managedDir = Join-Path $GameDir 'MiniMetro_Data\Managed'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$cecil = if ($CecilPath) {
    $CecilPath
} else {
    Join-Path (Split-Path $projectRoot -Parent) '.tools\Mono.Cecil.dll'
}
$distDir = Join-Path $projectRoot 'dist'
$stagingDir = Join-Path $projectRoot 'staging'
$backupAssembly = Join-Path $projectRoot 'backup\Assembly-CSharp.original.dll'
$installManifest = Join-Path $projectRoot 'backup\install-manifest.txt'
$gameAssembly = Join-Path $managedDir 'Assembly-CSharp.dll'

function Read-KeyValueFile([string]$Path) {
    $values = @{}
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $Path) {
            if ($line -match '^([^=]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
        }
    }
    return $values
}

$previousInstall = Read-KeyValueFile $installManifest
$currentGameHash = if (Test-Path -LiteralPath $gameAssembly -PathType Leaf) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $gameAssembly).Hash
} else { $null }
$baseAssembly = $gameAssembly
if (Test-Path -LiteralPath $backupAssembly -PathType Leaf) {
    $installedByUs = $previousInstall.ContainsKey('PatchedSHA256') `
        -and $currentGameHash -eq $previousInstall['PatchedSHA256']
    $restoredOriginal = $previousInstall.ContainsKey('OriginalSHA256') `
        -and $currentGameHash -eq $previousInstall['OriginalSHA256']
    if ($installedByUs -or $restoredOriginal) {
        $baseAssembly = $backupAssembly
    } else {
        Write-Warning 'The installed game assembly changed since the previous install; building from the current Steam assembly.'
    }
}

$required = @(
    $csc,
    $cecil,
    $baseAssembly,
    (Join-Path $managedDir 'UnityEngine.dll'),
    (Join-Path $managedDir 'UnityEngine.CoreModule.dll'),
    (Join-Path $managedDir 'UnityEngine.InputModule.dll'),
    (Join-Path $managedDir 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managedDir 'UnityEngine.TextRenderingModule.dll')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required build dependency not found: $path"
    }
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

$pluginOut = Join-Path $distDir 'MiniMetroExtremePlanner.dll'
$pluginSources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\MiniMetroExtremePlanner') `
    -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName
$pluginReferences = @(
    $baseAssembly,
    (Join-Path $managedDir 'UnityEngine.dll'),
    (Join-Path $managedDir 'UnityEngine.CoreModule.dll'),
    (Join-Path $managedDir 'UnityEngine.InputModule.dll'),
    (Join-Path $managedDir 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managedDir 'UnityEngine.TextRenderingModule.dll')
)
$pluginArgs = @('/nologo', '/target:library', '/optimize+', '/debug:pdbonly', '/codepage:65001', "/out:$pluginOut")
$pluginArgs += $pluginReferences | ForEach-Object { "/reference:$_" }
$pluginArgs += $pluginSources

Write-Host 'Building MiniMetroExtremePlanner.dll...'
& $csc $pluginArgs
if ($LASTEXITCODE -ne 0) { throw "Planner compilation failed with exit code $LASTEXITCODE" }

$patcherOut = Join-Path $distDir 'MiniMetroExtremePlannerPatcher.exe'
$patcherSource = Join-Path $projectRoot 'src\MiniMetroExtremePlannerPatcher\Program.cs'
$patcherArgs = @(
    '/nologo', '/target:exe', '/optimize+', '/debug:pdbonly', '/codepage:65001',
    "/out:$patcherOut", "/reference:$cecil", $patcherSource
)

Write-Host 'Building MiniMetroExtremePlannerPatcher.exe...'
& $csc $patcherArgs
if ($LASTEXITCODE -ne 0) { throw "Patcher compilation failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath $cecil -Destination (Join-Path $distDir 'Mono.Cecil.dll') -Force

$patchedAssembly = Join-Path $stagingDir 'Assembly-CSharp.dll'
& $patcherOut patch $baseAssembly $patchedAssembly $pluginOut $managedDir
if ($LASTEXITCODE -ne 0) { throw "Patch staging failed with exit code $LASTEXITCODE" }
& $patcherOut verify $patchedAssembly
if ($LASTEXITCODE -ne 0) { throw "Patch verification failed with exit code $LASTEXITCODE" }

$baseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $baseAssembly).Hash
$pluginHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pluginOut).Hash
$patchedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $patchedAssembly).Hash
@(
    "BuiltAt=$([DateTime]::Now.ToString('o'))"
    "GameDir=$GameDir"
    "BaseAssembly=$baseAssembly"
    "BaseSHA256=$baseHash"
    "GameAssemblySHA256=$currentGameHash"
    "PluginSHA256=$pluginHash"
    "PatchedSHA256=$patchedHash"
) | Set-Content -LiteralPath (Join-Path $stagingDir 'build-manifest.txt') -Encoding UTF8

Write-Host 'Build and staging verification complete.'
Write-Host "  Base: $baseAssembly"
Write-Host "  Plugin: $pluginOut"
Write-Host "  Patched: $patchedAssembly"
Write-Host "  Base SHA256: $baseHash"
Write-Host "  Plugin SHA256: $pluginHash"
Write-Host "  Patched SHA256: $patchedHash"
