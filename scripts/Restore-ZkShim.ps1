#requires -Version 5.1
<#
.SYNOPSIS
Restore the newest libzkfp.dll.bak.* over the deployed shim and clear the marker.
#>
[CmdletBinding()]
param(
    [string]$TargetDir = 'E:\Projects\mer'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$paths = Get-ZkDeployPaths -TargetDir $TargetDir
$TargetDir = $paths.TargetDir
if (-not (Test-Path -LiteralPath $TargetDir -PathType Container)) {
    throw "Target directory not found: $TargetDir"
}

Assert-TargetWritable -Path $TargetDir

$backups = @(Get-ChildItem -LiteralPath $TargetDir -Filter $paths.BackupFilter -File |
    # Backup names carry the creation stamp; file mtime carries the source DLL's build time.
    Sort-Object -Property Name -Descending)
if ($backups.Count -eq 0) {
    Write-Host "No backup found in $TargetDir matching $($paths.BackupFilter). Nothing to restore."
    return
}

$markerWasPresent = Test-Path -LiteralPath $paths.Marker -PathType Leaf
if (-not $markerWasPresent) {
    Write-Host "Warning: no shim marker present; restoring newest backup anyway."
}

$newest = $backups[0]
Copy-Item -LiteralPath $newest.FullName -Destination $paths.TargetDll -Force

if ($markerWasPresent) { Remove-Item -LiteralPath $paths.Marker -Force }

$hash = (Get-FileHash -LiteralPath $paths.TargetDll -Algorithm SHA256).Hash
Write-Host "Restored: $($newest.Name) to $($paths.TargetDll)"
Write-Host "SHA256: $hash"
Write-Host "Marker removed: $markerWasPresent"
