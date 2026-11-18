#requires -Version 5.1
<#
.SYNOPSIS
Opt-in deployment of the ZKTeco shim DLL into MER. Backs up the bundled DLL first.
#>
[CmdletBinding()]
param(
    [string]$ShimDll = 'zkshim\x64\Release\libzkfp.dll',
    [string]$TargetDir = 'E:\Projects\mer',
    [switch]$Backup
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$ShimDll = Resolve-ZkShimPath -ShimDll $ShimDll
$paths = Get-ZkDeployPaths -TargetDir $TargetDir
$TargetDir = $paths.TargetDir

if (-not (Test-Path -LiteralPath $ShimDll -PathType Leaf)) {
    throw "Shim DLL not found: $ShimDll. Build zkshim first (zkshim\x64\Release\libzkfp.dll)."
}
if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) {
    throw "Target directory not found: $TargetDir"
}
# A repeated deploy would back up the shim, and Restore could then never reach the genuine DLL.
if (Test-Path -LiteralPath $paths.Marker -PathType Leaf) {
    throw "Shim already deployed at $TargetDir. Run Restore-ZkShim.ps1 before deploying again."
}

Assert-TargetWritable -Path $TargetDir

$backupName = ''
if (Test-Path -LiteralPath $paths.TargetDll -PathType Leaf) {
    if (-not $Backup) {
        throw "Refusing to overwrite $($paths.TargetDll) (bundled ZKTeco DLL). Re-run with -Backup to save it first."
    }
    # Backup name is the creation stamp. LastWriteTimeUtc reflects the source DLL, not this copy.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupName = "$($paths.BackupPrefix)$stamp"
    $n = 1
    while (Test-Path -LiteralPath (Join-Path $TargetDir $backupName)) {
        # Zero-padded suffix keeps name-sort order past 9 collisions in one second.
        $backupName = "$($paths.BackupPrefix)$stamp-{0:D2}" -f $n
        $n++
    }
    Copy-Item -LiteralPath $paths.TargetDll -Destination (Join-Path $TargetDir $backupName)
    Write-Host "Backup saved: $backupName"
} else {
    Write-Host "No existing $($paths.TargetDll) to back up."
}

Copy-Item -LiteralPath $ShimDll -Destination $paths.TargetDll -Force

$hash = (Get-FileHash -LiteralPath $paths.TargetDll -Algorithm SHA256).Hash
@(
    "Deployed: $(Get-Date -Format o)"
    "Source: $ShimDll"
    "SourceSha256: $hash"
    "Backup: $(if ($backupName) { $backupName } else { 'none' })"
) | Set-Content -LiteralPath $paths.Marker -Encoding ASCII

Write-Host "Deployed shim: $($paths.TargetDll)"
Write-Host "SHA256: $hash"
Write-Host "Marker: $($paths.Marker)"
