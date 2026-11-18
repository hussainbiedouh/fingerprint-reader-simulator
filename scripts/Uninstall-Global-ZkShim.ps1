#requires -Version 5.1
<#
.SYNOPSIS
Remove the global KnownDLLs shim and restore both original libzkfp.dll files.
Requires a reboot; this script never reboots the machine.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

Assert-Admin
$p = Get-GlobalPaths
$current = Get-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -ErrorAction SilentlyContinue

if (-not (Test-Path -LiteralPath $p.StatePath -PathType Leaf)) {
    if ($null -ne $current -and [string]$current.($p.KnownDllsName) -ieq $p.KnownDllsData) {
        Remove-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -ErrorAction Stop
        $check = Get-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -ErrorAction SilentlyContinue
        if ($null -ne $check) { throw "KnownDLLs value removal could not be confirmed. DLLs were left untouched." }
        Write-Warning "No state file. Removed KnownDLLs value but left both DLLs untouched. Restore them manually from $($p.BackupDir)."
    } else {
        Write-Warning "No state file and no shim KnownDLLs value. No changes made."
    }
    return
}

try { $state = Get-Content -LiteralPath $p.StatePath -Raw | ConvertFrom-Json }
catch { throw "State file is unreadable: $($p.StatePath). $($_.Exception.Message)" }

$priorPresent = $false
$priorData = ''
if ($null -ne $state.KnownDlls) {
    $priorPresent = [bool]$state.KnownDlls.PriorValuePresent
    $priorData = [string]$state.KnownDlls.PriorValueData
}

# Drop or restore the KnownDLLs value first: a missing value is boot-safe even if a DLL restore fails.
if ($priorPresent) {
    New-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -PropertyType String -Value $priorData -Force | Out-Null
    Write-Host "Restored prior KnownDLLs value: $($p.KnownDllsName) = $priorData"
} elseif ($null -ne $current) {
    Remove-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -ErrorAction Stop
    Write-Host "Removed KnownDLLs value: $($p.KnownDllsName)"
} else {
    Write-Host "KnownDLLs value $($p.KnownDllsName) was already absent."
}
$after = Get-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -ErrorAction SilentlyContinue
$afterData = ''
if ($null -ne $after) { $afterData = [string]$after.($p.KnownDllsName) }
if ($afterData -ine $priorData) {
    throw "KnownDLLs value did not restore to its prior state (got '$afterData', expected '$priorData'). DLLs were not touched."
}

$complete = $true
if ($null -ne $state.PSObject.Properties['Complete']) { $complete = [bool]$state.Complete }
if (-not $complete) { Write-Warning 'Install did not complete; restoring only the DLLs recorded as copied.' }

foreach ($arch in @('x64', 'x86')) {
    $entry = $state.$arch
    if ($null -eq $entry) { throw "State file has no $arch section." }

    $modified = $true
    if ($null -ne $entry.PSObject.Properties['Modified']) { $modified = [bool]$entry.Modified }
    elseif ($null -ne $entry.PSObject.Properties['Copied']) { $modified = [bool]$entry.Copied }
    if (-not $modified) {
        Write-Host "Skipped ${arch}: install did not modify the DLL; left untouched."
        continue
    }

    $target = [string]$entry.Path
    $priorExisted = [bool]$entry.PriorExisted
    if (-not $priorExisted) {
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
            Write-Host "Deleted shim (no original DLL existed at install): $target"
        }
        continue
    }

    $backup = Join-Path $p.BackupDir ([string]$entry.Backup)
    if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) {
        throw "Backup missing: $backup. $target was left untouched; restore it manually."
    }
    Copy-Item -LiteralPath $backup -Destination $target -Force
    $expectedHash = [string]$entry.PriorSha256
    $actualHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($expectedHash -and $actualHash -ine $expectedHash) {
        throw "Restore hash mismatch at $target (got $actualHash, expected $expectedHash)."
    }
    Write-Host "Restored: $target from $backup"
    Write-Host "SHA256: $actualHash"
}

if (Test-Path -LiteralPath $p.MarkerPath -PathType Leaf) {
    Remove-Item -LiteralPath $p.MarkerPath -Force
    Write-Host "Marker removed: $($p.MarkerPath)"
}

Write-Host "KnownDLLs $($p.KnownDllsName): $(if ($afterData) { "restored to '$afterData'" } else { 'absent' })"
Write-Warning 'REBOOT REQUIRED so SMSS rebuilds KnownDLL sections without libzkfp. This script does NOT reboot. State and backups are kept for audit.'
