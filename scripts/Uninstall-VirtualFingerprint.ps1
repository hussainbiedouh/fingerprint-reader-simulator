#requires -Version 5.1
. (Join-Path $PSScriptRoot '_Common.ps1')

Assert-Admin

foreach ($hardwareId in $script:HardwareIds) {
    $devices = @(Get-VirtualFingerprintDevice -HardwareId $hardwareId)
    if ($devices.Count -eq 0) {
        Write-Host "No device with hardware ID $hardwareId; skipping."
        continue
    }
    foreach ($device in $devices) {
        & pnputil /remove-device $device.InstanceId
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to remove $($device.InstanceId) with exit code $LASTEXITCODE."
        } else {
            Write-Host "Removed device: $($device.InstanceId)"
        }
    }
}

$published = @(Get-VirtualFingerprintPublishedInf)

foreach ($oem in $published) {
    & pnputil /delete-driver $oem /uninstall /force
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to delete $oem with exit code $LASTEXITCODE."
    } else {
        Write-Host "Deleted driver package: $oem"
    }
}
if ($published.Count -eq 0) { Write-Host 'No VirtualFingerprint driver package in the driver store; skipping.' }

$remaining = @()
foreach ($hardwareId in $script:HardwareIds) {
    $remaining += @(Get-VirtualFingerprintDevice -HardwareId $hardwareId)
}
if ($remaining.Count -eq 0) {
    Write-Host 'Verified: no virtual fingerprint devices remain.'
} else {
    Write-Warning "$($remaining.Count) device(s) still present; a reboot may be required."
}
