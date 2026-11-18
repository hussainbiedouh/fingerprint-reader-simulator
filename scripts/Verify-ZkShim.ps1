#requires -Version 5.1
<#
.SYNOPSIS
Read-only status of the ZKTeco shim deployment in MER.
#>
[CmdletBinding()]
param(
    [string]$ShimDll = 'zkshim\x64\Release\libzkfp.dll',
    [string]$TargetDir = 'E:\Projects\mer'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$ShimDll = Resolve-ZkShimPath -ShimDll $ShimDll
$paths = Get-ZkDeployPaths -TargetDir $TargetDir

$backups = @()
if (Test-Path -LiteralPath $paths.TargetDir -PathType Container) {
    $backups = @(Get-ChildItem -LiteralPath $paths.TargetDir -Filter $paths.BackupFilter -File)
}

$shimHash = if (Test-Path -LiteralPath $ShimDll -PathType Leaf) { (Get-FileHash -LiteralPath $ShimDll -Algorithm SHA256).Hash } else { 'not present' }
$targetHash = if (Test-Path -LiteralPath $paths.TargetDll -PathType Leaf) { (Get-FileHash -LiteralPath $paths.TargetDll -Algorithm SHA256).Hash } else { 'not present' }

$report = @(
    [PSCustomObject]@{ Check = 'Target dir'; Result = $paths.TargetDir }
    [PSCustomObject]@{ Check = 'Source shim exists'; Result = (Test-Path -LiteralPath $ShimDll -PathType Leaf) }
    [PSCustomObject]@{ Check = 'Source shim SHA256'; Result = $shimHash }
    [PSCustomObject]@{ Check = 'Target DLL exists'; Result = (Test-Path -LiteralPath $paths.TargetDll -PathType Leaf) }
    [PSCustomObject]@{ Check = 'Target DLL SHA256'; Result = $targetHash }
    [PSCustomObject]@{ Check = 'Shim marker present'; Result = (Test-Path -LiteralPath $paths.Marker -PathType Leaf) }
    [PSCustomObject]@{ Check = 'Backup count'; Result = $backups.Count }
)

$report | Format-Table -AutoSize -Wrap | Out-String | Write-Host
