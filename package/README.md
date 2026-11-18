# Virtual fingerprint simulator - P0 gate

## Docs verified

- WBDI mandatory IOCTLs (all five implemented): `IOCTL_BIOMETRIC_GET_ATTRIBUTES`,
  `IOCTL_BIOMETRIC_RESET`, `IOCTL_BIOMETRIC_CALIBRATE`,
  `IOCTL_BIOMETRIC_GET_SENSOR_STATUS`, `IOCTL_BIOMETRIC_CAPTURE_DATA`.
  Local source of truth: `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared\winbio_ioctl.h`,
  section `// Mandatory IOCTLs`.
  https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/_biometric/
- CAPTURE_DATA size query and single-pending rule: DWORD-sized output returns required
  size immediately; second capture returns `WINBIO_E_DATA_COLLECTION_IN_PROGRESS`.
  https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/winbio_ioctl/ni-winbio_ioctl-ioctl_biometric_capture_data
- Cancel/completion synchronization (`WdfRequestMarkCancelable` /
  `WdfRequestUnmarkCancelable` / `EvtRequestCancel`):
  https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/synchronizing-cancel-and-completion-code
- UMDF 2 INF `DDInstall.Wdf`, `UmdfService`, `UmdfServiceOrder`, `UmdfLibraryVersion`,
  `ServiceBinary`; no coinstaller or `DriverCLSID`:
  https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/specifying-wdf-directives-in-inf-files
- WBDI INF registry values (`SensorMode`, `SystemSensor`, adapter binaries, `DatabaseId`,
  `DatabaseAddReg`, `Exclusive`, ACLs) mirrored from the WDK sample
  `wdk-samples\biometrics\driver\WudfBioUsbSample.inx` (UMDF 1 sample; settings verified
  against WinBio WBDI installation guidance).
  https://learn.microsoft.com/en-us/windows-hardware/drivers/biometric/

## Build

```
MSBuild.exe driver\VirtualFingerprint.sln /p:Configuration=Release /p:Platform=x64
```

UMDF 2 entry point is `FxDriverEntryUm`, exported by `wdf.h`; no `.def` file needed.
Project compiles against UMDF 2.15 headers/lib to match the INF framework version.
`VirtualFingerprint.inf` is stamped from the `.inx` with WDK `stampinf -u 2.15.0`;
`UmdfLibraryVersion=2.15.0`. Version verified from WDK 26100
`build\10.0.26100.0\WindowsDriver.UserMode.Default.props` (default `UMDF_VERSION_MINOR=15`
for UMDF 2 / Windows 10) and `WindowsDriver.Common.targets` (`UmdfVersion` =
`$(UMDF_VERSION_MAJOR).$(UMDF_VERSION_MINOR)`).

## Install / gate

INF copies both `VirtualFingerprint.dll` (to `%12%\UMDF`) and `EngineAdapter.dll`
(to `WinBioPlugins`) from the sensor `DDInstall`. The control device copies only
`UMDriverCopy`. Both DLLs are staged in `package\`.

Root devices: `ROOT\WFP_SENSOR` (WBDI interface, `Exclusive=1`, BA/SY) and
`ROOT\WFP_CONTROL` (private interface `{E5D15E8E-9EFC-4762-83E5-17407B10764A}`,
BA/SY/IU). Create with `pnputil /add-driver VirtualFingerprint.inf /install` then
`devcon install VirtualFingerprint.inf ROOT\WFP_SENSOR` (and the control device),
using a test-signed catalog. Windows Hello is out of scope.

Shared IOCTL codes, control interface GUID and `WFP_TEMPLATE_HEADER` live in
`include\VirtualFingerprintProtocol.h`.

## Package contents

Source (tracked): `VirtualFingerprint.inx` (tokenized template),
`VirtualFingerprint.inf` (stamped, no tokens), `README.md`, `.gitignore`.
Generated/staged in this folder: `VirtualFingerprint.dll`, `EngineAdapter.dll`
(copied from `driver\x64\Release` and `engine\x64\Release`), `VirtualFingerprint.cer`
(public test cert), `VirtualFingerprint.pfx` (private key, gitignored),
`VirtualFingerprint.cat` (signed catalog, gitignored).

## Regenerate (PowerShell, from repo root)

```powershell
# 1. Stage built binaries
Copy-Item driver\x64\Release\VirtualFingerprint.dll package\VirtualFingerprint.dll -Force
Copy-Item engine\x64\Release\EngineAdapter.dll package\EngineAdapter.dll -Force

# 2. Create self-signed test cert (once; exports public .cer + private .pfx)
$pwd = ConvertTo-SecureString -String '<local-test-password>' -Force -AsPlainText
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Virtual Fingerprint Simulator Test Signing' -KeyUsage DigitalSignature -HashAlgorithm SHA256 -KeyLength 2048 -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(5)
Export-Certificate -Cert $cert -FilePath package\VirtualFingerprint.cer -Force
Export-PfxCertificate -Cert $cert -FilePath package\VirtualFingerprint.pfx -Password $pwd -Force
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force

# 3. Generate catalog, then normalize the lowercase filename Inf2Cat emits
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x86\Inf2Cat.exe' "/driver:$PWD\package" /os:10_x64
Rename-Item package\virtualfingerprint.cat VirtualFingerprint.cat

# 4. Sign catalog (password: <local-test-password>)
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' sign /f package\VirtualFingerprint.pfx /p '<local-test-password>' /fd SHA256 package\VirtualFingerprint.cat

# 5. Verify
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' verify /v /pa package\VirtualFingerprint.cat
```

Keep the adapter-copy edits in both `.inx` and stamped `.inf`; `.inf` must stay
newer than `.inx`, otherwise `Get-InstallableInfPath` picks the tokenized `.inx`
and the token guard rejects `$UMDFVERSION$`. After any `.inx` edit, copy `.inx` to `.inf`, stamp the `.inf` copy with
`stampinf -u 2.15.0` (stamp the copy, not the template), then regenerate and
re-sign `.cat`.

Cert password: `<local-test-password>`. Public `.cer` must be installed into
`Root` and `TrustedPublisher` by `scripts\Install-VirtualFingerprint.ps1` before
install. Until then `signtool verify /pa` reports
`A certificate chain processed, but terminated in a root` (exit 1); after the
cert is trusted it reports `Successfully verified` (exit 0). Not timestamped.
Regenerate `.cat` whenever the INF or either DLL changes.
