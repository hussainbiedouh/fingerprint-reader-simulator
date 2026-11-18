#requires -Version 5.1
. (Join-Path $PSScriptRoot '_Common.ps1')

$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$os = "$($cv.ProductName) $($cv.DisplayVersion) (build $($cv.CurrentBuild).$($cv.UBR))"

$service = Get-Service -Name WbioSrvc -ErrorAction SilentlyContinue
$startMode = (Get-CimInstance Win32_Service -Filter "Name='WbioSrvc'" -ErrorAction SilentlyContinue).StartMode

$bcdState = Get-TestSigningState
if ($null -eq $bcdState) { $testSigning = 'unavailable (bcdedit requires admin)' }
elseif ($bcdState -ieq 'No') { $testSigning = 'Off (not set)' }
else { $testSigning = $bcdState }

try { $secureBoot = [string](Confirm-SecureBootUEFI) } catch { $secureBoot = "unknown ($($_.Exception.Message))" }

$wdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$devgen = Get-DevGenPath

$msbuild = $null
foreach ($base in @("${env:ProgramFiles}\Microsoft Visual Studio\2026", "${env:ProgramFiles}\Microsoft Visual Studio\2022", "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019")) {
    if (-not (Test-Path $base)) { continue }
    $msbuild = Get-ChildItem -Path $base -Recurse -Filter MSBuild.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\MSBuild\\[^\\]+\\Bin\\' } |
        Select-Object -First 1 -ExpandProperty FullName
    if ($msbuild) { break }
}

$report = @(
    [PSCustomObject]@{ Check = 'OS'; Result = $os }
    [PSCustomObject]@{ Check = 'Elevated (admin)'; Result = (Test-IsAdmin) }
    [PSCustomObject]@{ Check = 'WbioSrvc status'; Result = $service.Status }
    [PSCustomObject]@{ Check = 'WbioSrvc start type'; Result = $startMode }
    [PSCustomObject]@{ Check = 'TestSigning'; Result = $testSigning }
    [PSCustomObject]@{ Check = 'Secure Boot'; Result = $secureBoot }
    [PSCustomObject]@{ Check = 'WDK 10 installed'; Result = (Test-Path $wdkRoot) }
    [PSCustomObject]@{ Check = 'devgen.exe (x64)'; Result = $(if ($devgen) { $devgen } else { 'not found' }) }
    [PSCustomObject]@{ Check = 'MSBuild.exe'; Result = $(if ($msbuild) { $msbuild } else { 'not found' }) }
    [PSCustomObject]@{ Check = 'INF template (.inx)'; Result = (Test-Path -LiteralPath $script:InfTemplate) }
    [PSCustomObject]@{ Check = 'INF install file (.inf)'; Result = (Test-Path -LiteralPath $script:InfPath) }
    [PSCustomObject]@{ Check = 'Test certificate (.cer)'; Result = (Test-Path -LiteralPath $script:CertPath) }
)

$report | Format-Table -AutoSize -Wrap | Out-String | Write-Host
