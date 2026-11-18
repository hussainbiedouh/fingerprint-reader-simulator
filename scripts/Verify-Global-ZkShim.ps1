#requires -Version 5.1
<#
.SYNOPSIS
Read-only report on the global KnownDLL shim installation. Changes nothing.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$p = Get-GlobalPaths
$cv = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$os = "$($cv.ProductName) $($cv.DisplayVersion) (build $($cv.CurrentBuild).$($cv.UBR))"
$osArch = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }

function Get-SafeHash {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'not present' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$known = Get-ItemProperty -LiteralPath $p.KnownDllsKey -Name $p.KnownDllsName -ErrorAction SilentlyContinue
$knownData = ''
if ($null -ne $known) { $knownData = [string]$known.($p.KnownDllsName) }
$umci = Get-UmciEnforcementStatus

$loaded = [System.Collections.Generic.List[string]]::new()
foreach ($process in (Get-Process -ErrorAction SilentlyContinue)) {
    try {
        foreach ($module in $process.Modules) {
            if ($module.ModuleName -ieq 'libzkfp.dll') {
                $moduleHash = 'hash unavailable'
                try { $moduleHash = (Get-FileHash -LiteralPath $module.FileName -Algorithm SHA256).Hash } catch { }
                $loaded.Add("$($process.ProcessName) [$($process.Id)]: $($module.FileName) $moduleHash")
            }
        }
    } catch { } # Protected processes cannot be enumerated; they are not required for this report.
}
if ($loaded.Count -eq 0) { $loaded.Add('not loaded in any visible process') }

$dllBackups = @(Get-ChildItem -LiteralPath $p.BackupDir -Filter 'libzkfp-*.bak' -File -ErrorAction SilentlyContinue)
$regBackups = @(Get-ChildItem -LiteralPath $p.BackupDir -Filter 'KnownDLLs-*.reg' -File -ErrorAction SilentlyContinue)
$state = $null
if (Test-Path -LiteralPath $p.StatePath -PathType Leaf) {
    try { $state = Get-Content -LiteralPath $p.StatePath -Raw | ConvertFrom-Json } catch { $state = $null }
}

$report = @(
    [PSCustomObject]@{ Check = 'OS'; Result = $os }
    [PSCustomObject]@{ Check = 'OS architecture'; Result = $osArch }
    [PSCustomObject]@{ Check = 'Elevated (admin)'; Result = (Test-IsAdmin) }
    [PSCustomObject]@{ Check = 'UMCI enforcement (Win32_DeviceGuard)'; Result = "$(if ($umci -eq 2) { '2 (enforced; test-signed KnownDLLs blocked)' } elseif ($umci -lt 0) { 'unknown (install refuses)' } else { $umci })" }
    [PSCustomObject]@{ Check = "KnownDLLs $($p.KnownDllsName)"; Result = $(if ($knownData) { $knownData } else { 'absent' }) }
    [PSCustomObject]@{ Check = 'x64 shim'; Result = $p.X64Shim }
    [PSCustomObject]@{ Check = 'x64 shim SHA256'; Result = (Get-SafeHash -Path $p.X64Shim) }
    [PSCustomObject]@{ Check = 'x64 shim signature'; Result = (Get-ShimSignature -Path $p.X64Shim).Status }
    [PSCustomObject]@{ Check = 'x64 shim PE machine'; Result = $(if (Test-Path -LiteralPath $p.X64Shim -PathType Leaf) { Get-PeMachine -Path $p.X64Shim } else { 'not present' }) }
    [PSCustomObject]@{ Check = 'x86 shim'; Result = $p.X86Shim }
    [PSCustomObject]@{ Check = 'x86 shim SHA256'; Result = (Get-SafeHash -Path $p.X86Shim) }
    [PSCustomObject]@{ Check = 'x86 shim signature'; Result = (Get-ShimSignature -Path $p.X86Shim).Status }
    [PSCustomObject]@{ Check = 'x86 shim PE machine'; Result = $(if (Test-Path -LiteralPath $p.X86Shim -PathType Leaf) { Get-PeMachine -Path $p.X86Shim } else { 'not present' }) }
    [PSCustomObject]@{ Check = 'System32 DLL SHA256'; Result = (Get-SafeHash -Path $p.System32Dll) }
    [PSCustomObject]@{ Check = 'SysWOW64 DLL SHA256'; Result = (Get-SafeHash -Path $p.SysWOW64Dll) }
    [PSCustomObject]@{ Check = 'System32 hardlinks'; Result = (Get-HardlinkCount -Path $p.System32Dll) }
    [PSCustomObject]@{ Check = 'SysWOW64 hardlinks'; Result = (Get-HardlinkCount -Path $p.SysWOW64Dll) }
    [PSCustomObject]@{ Check = 'Loaded libzkfp.dll'; Result = ($loaded -join '; ') }
    [PSCustomObject]@{ Check = 'DLL backup count'; Result = $dllBackups.Count }
    [PSCustomObject]@{ Check = 'Registry export count'; Result = $regBackups.Count }
    [PSCustomObject]@{ Check = 'Install marker present'; Result = (Test-Path -LiteralPath $p.MarkerPath -PathType Leaf) }
    [PSCustomObject]@{ Check = 'State file present'; Result = ($null -ne $state) }
    [PSCustomObject]@{ Check = 'Install complete'; Result = $(if ($null -eq $state) { 'n/a' } elseif ($null -eq $state.PSObject.Properties['Complete']) { 'unknown' } else { [bool]$state.Complete }) }
    [PSCustomObject]@{ Check = 'State installed (UTC)'; Result = $(if ($state) { $state.InstalledUtc } else { 'n/a' }) }
)

$report | Format-Table -AutoSize -Wrap | Out-String | Write-Host
