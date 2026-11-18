# TriggerTool

Small .NET 10 console tool that drives the UMDF virtual fingerprint **control**
device. It installs a deterministic template and fires the read trigger that
completes a pending `IOCTL_BIOMETRIC_CAPTURE_DATA` request. Completes the P0
live gate together with `tests\WinBioProbe`.

## Build

```bat
cd tests\TriggerTool
dotnet build -c Release
```

Build is AnyCPU / `net10.0-windows`, `TreatWarningsAsErrors=true`.
`SIZE_T` is `IntPtr` and `SP_DEVICE_INTERFACE_DATA` is marshalled sequentially,
so enumeration and `SetupDiGetDeviceInterfaceDetail` are x64-safe (`cbSize` is
written as 8 on x64, 6 on x86).

## Commands

```bat
bin\Release\net10.0-windows\TriggerTool.exe set P0
bin\Release\net10.0-windows\TriggerTool.exe trigger
bin\Release\net10.0-windows\TriggerTool.exe fire P0
```

- `set <name-or-id>` - SHA-256 of the argument (UTF-8) gives the 16-byte template
  id (first 16 bytes) and a 64-byte deterministic template payload. Sends
  `IOCTL_WFP_SET_TEMPLATE`. Input buffer = `WFP_TEMPLATE_HEADER` (Size + Id)
  followed by the payload; `Size` is the total byte count (84 bytes).
- `trigger` - sends `IOCTL_WFP_TRIGGER_READ` (no input).
- `fire <name-or-id>` - runs `set` then `trigger`.

Every run prints the computed IOCTL hex codes (`0x0022A000` / `0x0022A004`),
the self-check result, the enumerated device path, and each IOCTL result.
`CTL_CODE` is computed in code from `FILE_DEVICE_UNKNOWN`, `METHOD_BUFFERED`
and `FILE_WRITE_ACCESS`; nothing is hard-coded. On any Win32 failure the tool
prints the exact error number and message from `Marshal.GetLastWin32Error` and
exits non-zero. It never guesses a cause.

## Using with WinBioProbe

Two terminals, both from the repository root:

1. Terminal A - start the waiting probe:
   ```bat
   cd tests\WinBioProbe
   bin\Release\net10.0-windows\probe.exe monitor
   ```
   It opens a monitor session for `WINBIO_EVENT_FP_UNCLAIMED_IDENTIFY` and a
   separate operation session, then waits.

2. Terminal B - install the template and fire the read:
   ```bat
   cd tests\TriggerTool
   bin\Release\net10.0-windows\TriggerTool.exe fire P0
   ```

   Use the same name in both the template and the engine's enrolled identity.
   The probe should receive the identify event and print the identity.

`trigger` only succeeds while a capture request is pending. Running it without
`probe.exe monitor` waiting will report the driver's Win32 error; that is
expected, not a tool failure. `set` works with or without the probe running.

## Device discovery

The tool enumerates interface paths for
`GUID_DEVINTERFACE_WFP_CONTROL {E5D15E8E-9EFC-4762-83E5-17407B10764A}` via
`SetupDiGetClassDevs` / `SetupDiEnumDeviceInterfaces` /
`SetupDiGetDeviceInterfaceDetail` (`DIGCF_PRESENT | DIGCF_DEVICEINTERFACE`),
then opens the first match with `CreateFileW` using read/write access and share
all. No fixed path and no registry access, matching the header's note that this
is not a WinBio class interface.

## Protocol source

`include\VirtualFingerprintProtocol.h`. IOCTLs derived from the `WFP_CTL_CODE`
macro there; do not change the header or this tool's derived values
independently.
