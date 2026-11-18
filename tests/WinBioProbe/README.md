# WinBioProbe

Acceptance probe for a waiting WinBio app detecting a virtual fingerprint read.

## Build / run

```bat
dotnet build -c Release
bin\Release\net10.0-windows\probe.exe list
bin\Release\net10.0-windows\probe.exe monitor
bin\Release\net10.0-windows\probe.exe identify
```

`list` prints the biometric-unit count and unit IDs. `identify` performs a direct
`WinBioLocateSensor` + `WinBioIdentify` (manual finger touch). `monitor` opens two
sessions: session A registers `WINBIO_EVENT_FP_UNCLAIMED_IDENTIFY`; session B runs the
identification after the event. The monitor session makes no other WinBio calls,
as required by `WinBioRegisterEventMonitor` docs for operation calls; it is only unregistered
and closed at shutdown. Every event pointer and the enumeration array are
released with `WinBioFree`.

## Bitness

Builds as AnyCPU. `SIZE_T` is declared `nuint`, all other WinBio scalars are fixed
width, so x86 and x64 hosts both marshal correctly. WinBio itself must be installed
and the process must run on Windows.

## Layout verification

`WinBioLayout.Verify()` compares CLR-marshalled sizes with SDK values at startup:
`WINBIO_IDENTITY` 76, `WINBIO_EVENT` 92, `WINBIO_UNIT_SCHEMA` 2588,
`WINBIO_BIR_DATA` 8, `WINBIO_BIR` 32.

## Docs used

- https://learn.microsoft.com/en-us/windows/win32/api/winbio/nf-winbio-winbioopensession
- https://learn.microsoft.com/en-us/windows/win32/api/winbio/nf-winbio-winbioregistereventmonitor
- https://learn.microsoft.com/en-us/windows/win32/api/winbio/nf-winbio-winbioenumbiometricunits
- https://learn.microsoft.com/en-us/windows/win32/api/winbio/nf-winbio-winbioidentify
- https://learn.microsoft.com/en-us/windows/win32/api/winbio/nf-winbio-winbiofree
- https://learn.microsoft.com/en-us/windows/win32/api/winbio/nc-winbio-pwinbio_event_callback
- https://learn.microsoft.com/en-us/windows/win32/secbiomet/winbio-event
- https://learn.microsoft.com/en-us/windows/win32/secbiomet/winbio-identity
- https://learn.microsoft.com/en-us/windows/win32/secbiomet/winbio-bir

Layout cross-checked against Windows SDK `WinBio.h` / `winbio_types.h`
(10.0.26100.0).
