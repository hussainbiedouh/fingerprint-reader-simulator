# EnrollTool

.NET 10 console tool that enrolls one deterministic fake fingerprint into the
Windows Biometric Service system-pool database through the virtual WBDI sensor.

## Build

```bat
cd tests\EnrollTool
dotnet build -c Release
```

## Commands

```bat
bin\Release\net10.0-windows\EnrollTool.exe P0
bin\Release\net10.0-windows\EnrollTool.exe list
```

`EnrollTool <name>` derives a 16-byte template id and 64-byte payload from
SHA-256 of the UTF-8 name (same algorithm as `TriggerTool`), stages the 84-byte
`WFP_TEMPLATE_HEADER` buffer with `IOCTL_WFP_SET_TEMPLATE`, then completes each
pending `IOCTL_BIOMETRIC_CAPTURE_DATA` with `IOCTL_WFP_TRIGGER_READ` while
`WinBioEnrollCapture` waits on a worker thread. On success it commits with
`WinBioEnrollCommit`, prints the resulting identity, and exits 0. WinBio HRESULT
failures print `0xXXXXXXXX` plus message and exit non-zero.

`list` enumerates fingerprint units without touching the database.

## Focus

System-pool enrollment requires biometric focus. `WinBioAcquireFocus` needs
Local System, so the tool falls back to the console foreground window. Launch it
from an interactive console that can hold foreground; a headless or detached
shell cannot grant focus, the tool exits 1 immediately with a focus error.

## Protocol

IOCTLs are derived in code from `CTL_CODE(FILE_DEVICE_UNKNOWN, fn,
METHOD_BUFFERED, FILE_WRITE_ACCESS)` as required by
`include\VirtualFingerprintProtocol.h`; a startup self-check verifies
`0x0022A000` / `0x0022A004`.
