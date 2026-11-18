# Windows Fingerprint Simulator

A Windows fingerprint reader simulator for development and testing.

It presents a virtual fingerprint reader to Windows through the Windows Biometric Framework (WBDI/WBF), plus a WinForms GUI to add named fingerprints and trigger reads. A waiting WinBio application receives fingerprint events as if a physical reader had been touched.

> Status: experimental. Uses a test-signed driver. Not for production authentication.

## What it does

- Registers a virtual UMDF 2 WBDI fingerprint sensor with the Windows Biometric Service
- Provides a separate control device for the GUI
- Lets you add named fingerprints with generated template bytes
- Stores fingerprints in `fingerprints.json` next to the executable
- Clicking a fingerprint completes a pending sensor capture and emits a fingerprint read event
- Includes a WinBio probe to verify that a waiting application receives `WINBIO_EVENT_FP_UNCLAIMED_IDENTIFY`

## Repository layout

| Path | Purpose |
| --- | --- |
| `driver/` | UMDF 2 virtual WBDI sensor driver |
| `engine/` | Windows Biometric Framework engine adapter |
| `include/` | Shared driver/engine protocol header |
| `package/` | INF, catalog/signing regeneration instructions |
| `src/` | GUI support code: JSON store, driver client, WinBio enrollment |
| `zkshim/` | Dual-arch ZKTeco shim DLL source and docs |
| `scripts/` | Setup, install, refresh, uninstall, verification scripts |
| `tests/WinBioProbe/` | Acceptance probe for WinBio events |
| `tests/TriggerTool/` | Sends template + trigger IOCTLs to the control device |
| `tests/EnrollTool/` | Enrolls a template into WBS from a foreground terminal |
| `docs/SETUP.md` | Full setup and troubleshooting guide |

## Requirements

- Windows 10/11 x64
- Visual Studio 2022 or newer with C++ desktop workload
- Windows Driver Kit 26100 or newer
- .NET SDK 10
- Administrator rights to install the test-signed driver
- Test signing enabled; Secure Boot may need to be disabled

See `docs/SETUP.md` for exact commands and warnings.

## Build

Driver:

```powershell
MSBuild.exe driver\VirtualFingerprint.sln /p:Configuration=Release /p:Platform=x64
```

Engine adapter:

```powershell
MSBuild.exe engine\EngineAdapter.sln /p:Configuration=Release /p:Platform=x64
```

GUI and tools:

```powershell
dotnet build -c Release
```

## Install

Run the scripts in `scripts/` from an elevated PowerShell:

```powershell
.\scripts\Setup-TestSigning.ps1
# reboot if requested
.\scripts\Enable-WbioSrvc.ps1
.\scripts\Install-VirtualFingerprint.ps1
```

Use `Refresh-VirtualFingerprint.ps1` after rebuilding driver or engine binaries.

To remove:

```powershell
.\scripts\Uninstall-VirtualFingerprint.ps1
```

## Run

```powershell
dotnet run --project .\WindowsFingerprintSimulator.csproj -c Release
```

Add a fingerprint by name, then click it to send a read event.

Verify with the probe:

```powershell
dotnet run --project .\tests\WinBioProbe\WinBioProbe.csproj -c Release -- list
dotnet run --project .\tests\WinBioProbe\WinBioProbe.csproj -c Release -- monitor
```

## ZKTeco shim mode

In addition to the WinBio reader, the simulator can act as a fake ZKTeco SDK reader. Build `zkshim\zkshim.sln` for x64 and Win32, choose `ZKTeco shim` in the GUI, and click a fingerprint to stage it. A `libzkfp.dll` shim running inside the target app sends `ACQUIRE` over `\\.\pipe\wfp-zk-shim` and receives a framed template.

### Global install (primary: any app sees the reader)

Register the signed shim as a KnownDLL. Any app that loads `libzkfp.dll` by bare name then gets the shim, with no per-app file changes:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-Global-ZkShim.ps1   # elevated
# reboot required
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Global-ZkShim.ps1
python .\tests\zk_known_dll_test.py   # elevated x64 Python
```

Remove with `Uninstall-Global-ZkShim.ps1` (elevated); it restores backed-up DLLs.

Coverage is both architectures: x64 DLL goes to `%SystemRoot%\System32`, x86 DLL to `%SystemRoot%\SysWOW64`. KnownDLLs applies only to bare-name loads such as `LoadLibrary("libzkfp.dll")` or `ctypes.WinDLL("libzkfp.dll")`; a full-path load bypasses it. A reboot is required because the loader reads the KnownDLL list at boot. See `docs\SETUP.md` for safe steps, backup, uninstall, and WinRE recovery.

### Per-app deploy (secondary: one app only)

For a single target app, deploy the DLL locally instead. This is explicit and reversible:

```powershell
.\scripts\Deploy-ZkShim.ps1 -Backup
.\scripts\Verify-ZkShim.ps1
.\scripts\Restore-ZkShim.ps1
```

The deploy script refuses to overwrite an existing `libzkfp.dll` unless `-Backup` is supplied. No kernel driver and no MER source edits are involved. See `zkshim\README.md` for the wire format and `tests\zk_shim_test.py` for an unattended integration test.

## Known limitations

- Windows Hello login is not supported; that requires a Microsoft biometric signature
- GUI-added fingerprints are saved for read simulation even if WBS enrollment fails
- WBS identity mapping is reliable only for templates enrolled through `tests/EnrollTool`
- Test-signed driver requires local test-signing configuration
- ZKTeco support is a fake SDK shim, not USB-level hardware emulation
- KnownDLL override affects bare-name loads only; full-path loads bypass it and a reboot is required after install
- The shim must not be deployed over a real ZKTeco SDK unless you intend to test with it

## Security

Do not use this simulator for real authentication. See `SECURITY.md`.

## License

MIT. See `LICENSE`.
