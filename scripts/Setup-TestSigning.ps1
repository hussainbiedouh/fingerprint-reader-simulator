#requires -Version 5.1
. (Join-Path $PSScriptRoot '_Common.ps1')

Assert-Admin

$state = Get-TestSigningState
if ($null -eq $state) { throw 'Unable to read boot configuration with bcdedit /enum {current}.' }
Write-Host "Current TestSigning state: $state"

try {
    Write-Host "Secure Boot: $(Confirm-SecureBootUEFI)"
} catch {
    Write-Warning "Secure Boot state unavailable: $($_.Exception.Message)"
}

if ($state -ieq 'Yes') {
    Write-Host 'TestSigning already enabled. No change made.'
} else {
    & bcdedit /set testsigning on
    if ($LASTEXITCODE -ne 0) {
        throw "bcdedit failed with exit code $LASTEXITCODE. If Secure Boot policy blocked the change, disable Secure Boot in BIOS/UEFI (and suspend BitLocker) before retrying."
    }
    $after = Get-TestSigningState
    if ($null -eq $after -or $after -ine 'Yes') {
        throw "TestSigning change could not be confirmed (state: $after)."
    }
    Write-Host "TestSigning after change: $after"
}

Write-Warning 'Reboot required for TestSigning to take effect. This script does NOT reboot the machine.'
