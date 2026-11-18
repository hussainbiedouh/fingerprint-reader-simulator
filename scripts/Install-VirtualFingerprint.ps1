#requires -Version 5.1
. (Join-Path $PSScriptRoot '_Common.ps1')

Assert-Admin

$inf = Get-InstallableInfPath
Write-Host "Using INF: $inf"

$cat = [IO.Path]::ChangeExtension($inf, '.cat')
if (-not (Test-Path -LiteralPath $cat)) { throw "Catalog missing at $cat. Generate and sign it (see package\README.md)." }

if (Test-Path -LiteralPath $script:CertPath) {
    try {
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($script:CertPath)
    } catch {
        throw "Cannot read test certificate $($script:CertPath): $($_.Exception.Message)"
    }
    foreach ($storeName in @('Root', 'TrustedPublisher')) {
        $storePath = "Cert:\LocalMachine\$storeName"
        $existing = @(Get-ChildItem -LiteralPath $storePath -ErrorAction Stop |
            Where-Object { $_.Thumbprint -eq $cert.Thumbprint })
        if ($existing.Count -eq 0) {
            Import-Certificate -FilePath $script:CertPath -CertStoreLocation $storePath -ErrorAction Stop | Out-Null
            Write-Host "Installed test certificate into $storePath."
        } else {
            Write-Host "Test certificate already present in $storePath."
        }
    }
} else {
    Write-Warning "Certificate not found at $($script:CertPath); skipping certificate install."
}

$devgen = Get-DevGenPath
if (-not $devgen) { throw 'devgen.exe not found under Windows Kits 10 Tools\x64. Install WDK 26100 or later.' }

foreach ($hardwareId in $script:HardwareIds) {
    $existing = @(Get-VirtualFingerprintDevice -HardwareId $hardwareId)
    if ($existing.Count -gt 0) {
        Write-Host "Device already present: $hardwareId ($($existing[0].InstanceId)); skipping devgen."
        continue
    }
    & $devgen /add /bus ROOT /hardwareid $hardwareId
    if ($LASTEXITCODE -ne 0) { throw "devgen failed for $hardwareId with exit code $LASTEXITCODE." }
    Write-Host "Created root device: $hardwareId"
}

$addOutput = & pnputil /add-driver $inf /install 2>&1
$addExit = $LASTEXITCODE
$addText = $addOutput | Out-String
# pnputil can return non-zero yet report the package is already current; that is success.
$upToDate = $addText -match 'up-to-date|already exists|Added driver packages:\s*0'
if ($addExit -ne 0 -and -not $upToDate) { throw "pnputil /add-driver failed with exit code $addExit : $addText" }
if ($upToDate) { Write-Host 'pnputil reports driver package already present/up-to-date; treating as success.' }

foreach ($hardwareId in $script:HardwareIds) {
    $devices = @(Get-VirtualFingerprintDevice -HardwareId $hardwareId)
    if ($devices.Count -eq 0) {
        Write-Warning "No device found for $hardwareId. Check %windir%\inf\setupapi.dev.log."
        continue
    }
    $devices | Select-Object HardwareID, InstanceId, Name, Status, ConfigManagerErrorCode |
        Format-List | Out-String | Write-Host
}
