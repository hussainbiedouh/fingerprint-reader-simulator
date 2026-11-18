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

## Known limitations

- Windows Hello login is not supported; that requires a Microsoft biometric signature
- GUI-added fingerprints are saved for read simulation even if WBS enrollment fails
- WBS identity mapping is reliable only for templates enrolled through `tests/EnrollTool`
- Test-signed driver requires local test-signing configuration
- This project simulates WBF/WBDI only; it does not emulate vendor USB fingerprint protocols

## Security

Do not use this simulator for real authentication. See `SECURITY.md`.

## License

MIT. See `LICENSE`.
