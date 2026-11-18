# Shared paths, hardware IDs, and guards. Dot-source from the other scripts.

$script:RepoRoot         = Split-Path -Parent $PSScriptRoot
$script:PackageDir       = Join-Path $script:RepoRoot 'package'
$script:InfTemplate      = Join-Path $script:PackageDir 'VirtualFingerprint.inx'
$script:InfPath          = Join-Path $script:PackageDir 'VirtualFingerprint.inf'
$script:CertPath         = Join-Path $script:PackageDir 'VirtualFingerprint.cer'
$script:SensorHardwareId = 'ROOT\WFP_SENSOR'
$script:ControlHardwareId = 'ROOT\WFP_CONTROL'
$script:HardwareIds      = @($script:SensorHardwareId, $script:ControlHardwareId)

$script:ZkTargetDllName = 'libzkfp.dll'
$script:ZkMarkerName    = 'libzkfp.shim.deployed'
$script:ZkBackupPrefix  = 'libzkfp.dll.bak.'

function Test-IsAdmin {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Admin {
    if (-not (Test-IsAdmin)) {
        throw 'Administrator rights required. Right-click PowerShell and select "Run as administrator".'
    }
}

function Assert-TargetWritable {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    foreach ($root in @(${env:ProgramFiles}, ${env:ProgramFiles(x86)})) {
        if (-not $root) { continue }
        $rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\')
        if ($full -ieq $rootFull -or $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) { Assert-Admin; return }
    }
}

function Resolve-ZkShimPath {
    param([Parameter(Mandatory = $true)][string]$ShimDll)
    if (-not [IO.Path]::IsPathRooted($ShimDll)) { $ShimDll = Join-Path $script:RepoRoot $ShimDll }
    return [IO.Path]::GetFullPath($ShimDll)
}

function Get-ZkDeployPaths {
    param([Parameter(Mandatory = $true)][string]$TargetDir)
    $dir = [IO.Path]::GetFullPath($TargetDir)
    return [PSCustomObject]@{
        TargetDir    = $dir
        TargetDll    = Join-Path $dir $script:ZkTargetDllName
        Marker       = Join-Path $dir $script:ZkMarkerName
        BackupPrefix = $script:ZkBackupPrefix
        BackupFilter = "$($script:ZkBackupPrefix)*"
    }
}

function Get-TestSigningState {
    $output = & bcdedit /enum '{current}' 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }
    $line = @($output | Where-Object { "$_" -match '^\s*testsigning\s+(\S+)' }) | Select-Object -First 1
    if ($line -and "$line" -match '^\s*testsigning\s+(\S+)') { return $Matches[1] }
    return 'No'
}

function Get-DevGenPath {
    $toolsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Tools'
    if (-not (Test-Path $toolsRoot)) { return $null }
    return (Get-ChildItem -Path $toolsRoot -Filter devgen.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1 -ExpandProperty FullName)
}

function Get-VirtualFingerprintDevice {
    param([Parameter(Mandatory = $true)][string]$HardwareId)
    try {
        # Win32_PnPEntity names the instance ID DeviceID; expose it under the PnP cmdlet name.
        return @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop |
            Where-Object { $_.HardwareID -contains $HardwareId } |
            Select-Object @{Name = 'InstanceId'; Expression = { $_.DeviceID } }, HardwareID, Name, Status, ConfigManagerErrorCode)
    } catch {
        throw "Unable to query PnP devices via WMI: $($_.Exception.Message)"
    }
}

function Get-VirtualFingerprintPublishedInf {
    # Published oem*.inf whose source INF is VirtualFingerprint.inf. pnputil works without elevation; DISM may not.
    $sourceName = [IO.Path]::GetFileName($script:InfPath)
    $found = [System.Collections.Generic.List[string]]::new()

    $current = $null
    foreach ($line in (& pnputil /enum-drivers 2>&1)) {
        if ($line -match '^\s*Published Name:\s*(\S+)') { $current = $Matches[1]; continue }
        if ($line -match '^\s*Original Name:\s*(\S+)' -and
            [IO.Path]::GetFileName($Matches[1]) -ieq $sourceName -and
            $current -match '^oem\d+\.inf$') {
            if (-not $found.Contains($current)) { $found.Add($current) }
        }
    }

    try {
        Get-WindowsDriver -Online -ErrorAction Stop |
            Where-Object { [IO.Path]::GetFileName("$($_.OriginalFileName)") -ieq $sourceName -and $_.Driver -match '^oem\d+\.inf$' } |
            ForEach-Object { if (-not $found.Contains($_.Driver)) { $found.Add($_.Driver) } }
    } catch { } # DISM is supplementary; pnputil already covered the store.

    return @($found)
}

function Get-InstallableInfPath {
    # pnputil /add-driver accepts *.inf only; stage the .inx template as a sibling .inf when needed.
    $hasTemplate = Test-Path -LiteralPath $script:InfTemplate
    $hasInstallFile = Test-Path -LiteralPath $script:InfPath
    if (-not $hasTemplate -and -not $hasInstallFile) {
        throw "INF missing. Expected $($script:InfPath) or $($script:InfTemplate)."
    }
    # A newer .inf is the build output and wins; a newer .inx regenerates the staging copy.
    $candidate = $script:InfPath
    if ($hasTemplate -and (-not $hasInstallFile -or
        (Get-Item -LiteralPath $script:InfTemplate).LastWriteTimeUtc -gt (Get-Item -LiteralPath $script:InfPath).LastWriteTimeUtc)) {
        $candidate = $script:InfTemplate
    }

    $unresolved = Select-String -LiteralPath $candidate -Pattern '^\s*[^;].*\$[A-Z0-9_]+\$' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($unresolved) {
        throw "Unresolved token '$($unresolved.Matches[0].Value)' in $candidate. Expand it with WDK stampinf (its -u option sets UMDF version tokens) or ship a generated $($script:InfPath)."
    }
    if ($candidate -ne $script:InfPath) { Copy-Item -LiteralPath $candidate -Destination $script:InfPath -Force }
    return $script:InfPath
}
