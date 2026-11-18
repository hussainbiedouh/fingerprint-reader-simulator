#requires -Version 5.1
. (Join-Path $PSScriptRoot '_Common.ps1')

Assert-Admin

try {
    Set-Service -Name WbioSrvc -StartupType Manual -ErrorAction Stop
    Start-Service -Name WbioSrvc -ErrorAction Stop
} catch {
    throw "Failed to configure or start WbioSrvc: $($_.Exception.Message)"
}

$service = Get-Service -Name WbioSrvc -ErrorAction Stop
try {
    $startMode = (Get-CimInstance Win32_Service -Filter "Name='WbioSrvc'" -ErrorAction Stop).StartMode
} catch {
    $startMode = $null
}

$reportedMode = if ($startMode) { $startMode } else { 'unavailable' }
if ($service.Status -ne 'Running' -or ($startMode -and $startMode -ne 'Manual')) {
    throw "Verification failed: WbioSrvc Status=$($service.Status), StartMode=$reportedMode."
}

Write-Host "WbioSrvc verified: Status=$($service.Status), StartMode=$reportedMode."
