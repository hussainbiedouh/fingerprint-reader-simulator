#requires -Version 5.1
<#
.SYNOPSIS
Install the signed x64/x86 ZKTeco shims globally via a KnownDLLs value.
Requires a reboot; this script never reboots the machine.
#>
[CmdletBinding()]
param(
    [switch]$AllowHardlinkRewrite
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

Assert-Admin
$p = Get-GlobalPaths

# Everything that can refuse the install runs before any system file is touched.
$targets = @(
    [PSCustomObject]@{ Arch = 'x64'; Shim = $p.X64Shim; Machine = '0x8664'; Target = $p.System32Dll; Hash = '' }
    [PSCustomObject]@{ Arch = 'x86'; Shim = $p.X86Shim; Machine = '0x014C'; Target = $p.SysWOW64Dll; Hash = '' }
)
foreach ($item in $targets) {
    if (-not (Test-Path -LiteralPath $item.Shim -PathType Leaf)) {
        throw "Shim not found: $($item.Shim). Build both architectures first."
    }
    $signature = Get-ShimSignature -Path $item.Shim
    if ($signature.Status -ne 'Valid') {
        throw "Signature of $($item.Shim) is '$($signature.Status)' (signer '$($signature.Subject)'). Trust the test cert in LocalMachine Root/TrustedPublisher first."
    }
    $machine = Get-PeMachine -Path $item.Shim
    if ($machine -ine $item.Machine) {
        throw "Wrong architecture for $($item.Shim): PE machine $machine, expected $($item.Machine)."
    }
    $item.Hash = (Get-FileHash -LiteralPath $item.Shim -Algorithm SHA256).Hash
}

$umci = Get-UmciEnforcementStatus
if ($umci -eq 2 -or $umci -lt 0) {
    throw "UMCI enforcement status is $umci (2 enforced, -1 unknown); test-signed KnownDLLs cannot load. Refusing to install."
}

if (Test-Path -LiteralPath $p.MarkerPath -PathType Leaf) {
    throw "Global shim is already installed (marker $($p.MarkerPath)). Run Uninstall-Global-ZkShim.ps1 first."
}
foreach ($target in @($p.System32Dll, $p.SysWOW64Dll)) {
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if (@($targets.Hash) -contains $hash) {
        throw "$target already contains a shim. Run Uninstall-Global-ZkShim.ps1 before installing again."
    }
}
foreach ($item in $targets) {
    if (-not (Test-Path -LiteralPath $item.Target -PathType Leaf)) { continue }
    $links = Get-HardlinkCount -Path $item.Target
    if ($links -lt 0) { throw "Cannot enumerate hardlinks for $($item.Target); refusing to overwrite." }
    if ($links -gt 1 -and -not $AllowHardlinkRewrite) {
        throw "$($item.Target) has $links hardlinks. Re-run with -AllowHardlinkRewrite to delete-then-copy (this breaks the other links)."
    }
}

$priorValue = Get-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -ErrorAction SilentlyContinue
$priorValuePresent = $null -ne $priorValue
$priorValueData = ''
if ($priorValuePresent) { $priorValueData = [string]$priorValue.($p.KnownDllsName) }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Path $p.BackupDir -Force | Out-Null

# Read-only backup of the whole key must exist before any DLL is replaced.
$regName = Get-GlobalBackupName -Prefix "KnownDLLs-$stamp" -Extension 'reg' -Directory $p.BackupDir
$regBackup = Join-Path $p.BackupDir $regName
$regError = ''
try { & reg export $p.KnownDllsRegPath $regBackup /y 2>&1 | Out-Null } catch { $regError = $_.Exception.Message }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $regBackup -PathType Leaf)) {
    throw "Failed to export $($p.KnownDllsRegPath) to $regBackup ($regError). No DLL was changed."
}
Write-Host "Registry backup: $regBackup"

$stateTargets = @{}
foreach ($item in $targets) {
    $existed = Test-Path -LiteralPath $item.Target -PathType Leaf
    $priorHash = ''
    if ($existed) { $priorHash = (Get-FileHash -LiteralPath $item.Target -Algorithm SHA256).Hash }
    $backupName = ''
    if ($existed) { $backupName = Get-GlobalBackupName -Prefix "libzkfp-$($item.Arch)-$stamp" -Extension 'bak' -Directory $p.BackupDir }
    $stateTargets[$item.Arch] = [ordered]@{
        Path         = $item.Target
        ShimPath     = $item.Shim
        ShimSha256   = $item.Hash
        PriorExisted = $existed
        PriorSha256  = $priorHash
        Backup       = $backupName
        Modified     = $false
        Copied       = $false
    }
}

function Save-GlobalState {
    param([bool]$Complete)
    $state = [ordered]@{
        InstalledUtc          = (Get-Date).ToUniversalTime().ToString('o')
        Stamp                 = $stamp
        Complete              = $Complete
        UmciEnforcementStatus = $umci
        Marker                = $p.MarkerPath
        X64                   = $stateTargets['x64']
        X86                   = $stateTargets['x86']
        KnownDlls             = [ordered]@{
            Key               = $p.KnownDllsKey
            ValueName         = $p.KnownDllsName
            ValueData         = $p.KnownDllsData
            PriorValuePresent = $priorValuePresent
            PriorValueData    = $priorValueData
            RegistryBackup    = $regName
        }
    }
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $p.StatePath -Encoding UTF8
}

# State exists before the first write; Uninstall can recover a partial install.
Save-GlobalState -Complete $false

foreach ($item in $targets) {
    $entry = $stateTargets[$item.Arch]
    if ($entry.PriorExisted) {
        $backupPath = Join-Path $p.BackupDir $entry.Backup
        Copy-Item -LiteralPath $item.Target -Destination $backupPath
        $backedUpHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
        if ($backedUpHash -ine $entry.PriorSha256) {
            throw "Backup hash mismatch for $($item.Target). No DLL was changed for this architecture; see $($p.StatePath)."
        }
        Write-Host "Backup: $backupPath"
    } else {
        Write-Host "No original DLL at $($item.Target); uninstall will delete the shim."
    }
    # Mark modified before any unlink or in-place copy: a failure here still needs restore.
    $entry.Modified = $true
    Save-GlobalState -Complete $false
    # A hardlinked target must be unlinked before copy, or all other links change too.
    if ($entry.PriorExisted -and (Get-HardlinkCount -Path $item.Target) -gt 1) { Remove-Item -LiteralPath $item.Target -Force }
    Copy-Item -LiteralPath $item.Shim -Destination $item.Target -Force
    $actual = (Get-FileHash -LiteralPath $item.Target -Algorithm SHA256).Hash
    if ($actual -ine $item.Hash) {
        throw "Hash mismatch at $($item.Target) after copy (got $actual). Run Uninstall-Global-ZkShim.ps1; the KnownDLLs value has not been written."
    }
    $entry.Copied = $true
    Save-GlobalState -Complete $false
}

# Written last: SMSS consumes KnownDLLs at boot, so DLLs and backups must be verified first.
New-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -PropertyType String -Value $p.KnownDllsData -Force | Out-Null
$written = Get-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -ErrorAction Stop
if ([string]$written.($p.KnownDllsName) -ine $p.KnownDllsData) {
    throw "KnownDLLs value did not verify. Run Uninstall-Global-ZkShim.ps1 immediately."
}
Save-GlobalState -Complete $true
Set-Content -LiteralPath $p.MarkerPath -Value "Installed: $(Get-Date -Format o)" -Encoding ASCII

Write-Host "Installed KnownDLLs $($p.KnownDllsName) = $($p.KnownDllsData)"
Write-Host "State: $($p.StatePath)"
Write-Warning 'REBOOT REQUIRED for SMSS to build the new KnownDLL section. This script does NOT reboot. Until then, bare-name libzkfp.dll loads still use the old search order.'
foreach ($arch in @('x64', 'x86')) {
    $entry = $stateTargets[$arch]
    if ($entry.Backup) {
        Write-Warning "WinRE recovery for ${arch}: copy '$((Join-Path $p.BackupDir $entry.Backup))' over '$($entry.Path)'"
    } else {
        Write-Warning "WinRE recovery for ${arch}: delete '$($entry.Path)' (no original DLL existed at install)"
    }
}
Write-Warning ('WinRE: then run reg delete "{0}" /v {1} /f (point HKLM\SYSTEM at the mounted hive if needed), and reboot.' -f $p.KnownDllsRegPath, $p.KnownDllsName)
