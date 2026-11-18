#requires -Version 5.1
. (Join-Path $PSScriptRoot '_Common.ps1')

Assert-Admin

Write-Host '=== Uninstall existing driver ==='
& (Join-Path $PSScriptRoot 'Uninstall-VirtualFingerprint.ps1')

# Remove any published package still left in the store after uninstall.
foreach ($oem in @(Get-VirtualFingerprintPublishedInf)) {
    & pnputil /delete-driver $oem /uninstall /force
    if ($LASTEXITCODE -ne 0) { throw "Failed to delete leftover driver package $oem with exit code $LASTEXITCODE." }
    Write-Host "Deleted leftover driver package: $oem"
}

Write-Host '=== Install current driver ==='
& (Join-Path $PSScriptRoot 'Install-VirtualFingerprint.ps1')
