# ZKTeco libzkfp.dll shim

Minimal x64 and x86 `libzkfp.dll` shims that satisfy the `ZKFPM_*` bindings
used by MER's clean-room ctypes reader (`E:/Projects/mer/ZktecoZkfReader.py`).
They return one virtual device, serve deterministic captures from a named pipe
or a config file, and perform template matching locally. No real ZKTeco SDK is
involved. User mode only: no kernel driver, no USB emulation, no MER source or
file edits.

Built with VS18 (`v145`) + WDK 26100, `Release`, for two architectures: x64
and Win32 (x86). Both DLLs carry the same 16 functions.

## Build

```
MSBuild.exe zkshim\zkshim.sln /p:Configuration=Release /p:Platform=x64
MSBuild.exe zkshim\zkshim.sln /p:Configuration=Release /p:Platform=Win32
```

Outputs:

| Platform | DLL | Deployment destination |
| --- | --- | --- |
| `x64` | `zkshim\x64\Release\libzkfp.dll` | `%WINDIR%\System32\libzkfp.dll` |
| `Win32` | `zkshim\Win32\Release\libzkfp.dll` | `%WINDIR%\SysWOW64\libzkfp.dll` |

Placement is owned by `scripts\Install-Global-ZkShim.ps1` (system-wide) or
`scripts\Deploy-ZkShim.ps1` (one app); this project never copies a DLL into a
system or app directory. The build performs no system install.

Verify exports with `dumpbin /exports <dll>`. The x64 DLL must show all 16
names below undecorated. The Win32 DLL must show those 16 bare names **plus**
16 decorated `_ZKFPM_*@N` aliases (32 total).

## Signing

Both DLLs are signed with the local test PFX (SHA256). The cert is already in
the local Trusted Root / Local Machine stores, so `signtool verify /pa` passes.

```
signtool sign /f package\VirtualFingerprint.pfx /fd SHA256 zkshim\x64\Release\libzkfp.dll
signtool sign /f package\VirtualFingerprint.pfx /fd SHA256 zkshim\Win32\Release\libzkfp.dll
signtool verify /pa zkshim\x64\Release\libzkfp.dll
signtool verify /pa zkshim\Win32\Release\libzkfp.dll
```

`signtool` prompts for the local test certificate password; do not store it
here. Re-sign after every rebuild: MSBuild overwrites the signed image and drops
the signature.

## Global install (KnownDLLs)

Primary mode: any app that loads `libzkfp.dll` by bare name sees the shim.
`scripts\Install-Global-ZkShim.ps1` (elevated) copies the signed x64 DLL to
`%WINDIR%\System32\libzkfp.dll`, the signed x86 DLL to
`%WINDIR%\SysWOW64\libzkfp.dll`, and adds `libzkfp` = `libzkfp.dll` under
`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs`.

The Windows loader reads that list at boot, so a **reboot is required**. The
override applies to bare-name loads such as `ctypes.WinDLL('libzkfp.dll')` or
`LoadLibrary(L"libzkfp.dll")` only; a full-path load bypasses KnownDLLs and
loads that path instead. Both architectures are covered by the single
registration: 64-bit processes resolve `System32`, 32-bit processes resolve
`SysWOW64`.

This is user mode only: no kernel driver, no USB-level emulation, no MER source
or file edits. For a single target app, `scripts\Deploy-ZkShim.ps1` (with
`-Backup`) and `scripts\Restore-ZkShim.ps1` remain the secondary per-app option.

Backups of replaced DLLs and the registry value go to
`%ProgramData%\WindowsFingerprintSimulator\backups\`. Uninstall with
`scripts\Uninstall-Global-ZkShim.ps1` (elevated), then reboot. A bad entry can
block boot; WinRE recovery is in `docs\SETUP.md`.

## Exports

| Function | Behavior |
| --- | --- |
| `ZKFPM_Init` | Returns 0 (success). |
| `ZKFPM_Terminate` | Returns 0. |
| `ZKFPM_GetDeviceCount` | Returns 1. |
| `ZKFPM_OpenDevice` | Index 0 returns handle 1; other indexes return NULL. |
| `ZKFPM_CloseDevice` | Returns 0 for the open handle, `-7` otherwise. |
| `ZKFPM_SetParameters` | No-op, returns 0. |
| `ZKFPM_GetParameters` | Zeroes up to `*pcbValue` bytes, returns 0. |
| `ZKFPM_GetCaptureParamsEx` | Writes 256, 256, 500; returns 0. |
| `ZKFPM_AcquireFingerprint` | Pipe/file capture; writes template and 65536 deterministic image bytes; returns 0 or nonzero when no capture is available. |
| `ZKFPM_DBInit` | Returns a fresh, non-null DB handle. |
| `ZKFPM_DBFree` | Drops that handle's entire fid map; returns 0. |
| `ZKFPM_DBAdd` | Stores `fid -> payload` in the calling DB's map; returns 0. |
| `ZKFPM_DBDel` | Removes a fid; returns 0. |
| `ZKFPM_DBIdentify` | On frame match writes `fid` and `score = 99`, returns 0; otherwise writes zeros and returns `-20`. |
| `ZKFPM_DBMatch` | Returns 0 on frame match, `-20` on no match. |
| `ZKFPM_DBMerge` | Copies the 3 framed inputs (lengths read from their headers) into the output; returns 0, `-11` if the output buffer is too small. |

All exports use `__stdcall` and C linkage. On x64 the calling convention is
unified and names are undecorated, which matches `ctypes.WinDLL` (stdcall)
loading by bare name.

**x86 calling convention: stdcall only.** The Win32 DLL is `__stdcall`; cdecl
x86 (`ctypes.CDLL` / `__cdecl`) is **not supported** and must not be used — a
cdecl caller would not pop the callee's arguments and would corrupt the stack.
Use `ctypes.WinDLL` on both architectures. The Win32 DLL publishes each
function twice (same address): the bare name for `GetProcAddress`/`WinDLL`
lookup, and the decorated `_ZKFPM_*@N` symbol, where `N` is the exact number of
argument bytes popped on x86. Verified counts: `@0` for the zero-arg functions
(`Init`, `Terminate`, `GetDeviceCount`, `DBInit`), `@4` for one pointer/int
(`OpenDevice`, `CloseDevice`, `DBFree`), `@8` for `DBDel` (2 args), `@16` for the
four-arg setters/getters and `DBAdd`, `@20` for the five-arg capture/DB match
functions, and `@24` for `DBMerge` (6 args). Aliases are defined in
`zkshim-x86.def`; x64 uses `zkshim.def`.

## Template framing

Every template payload is opaque and at most 2048 bytes. Records are
self-framing so a merged template needs no side-channel lengths:

```
offset 0  : 'M' 'Z' 'K' 'F'            (4-byte magic)
offset 4  : uint32 little-endian length N of the payload
offset 8  : N payload bytes            (current records use N = 64)
```

A single record is therefore 72 bytes. A merged payload is the concatenation
of three records (216 bytes), still well under 2048. Every function receiving
a template validates the magic, the declared length, the buffer bounds, and
that the whole buffer is covered by whole frames. `DBIdentify` and `DBMatch`
match when any frame of one template equals any frame of the other, i.e. the
fresh capture equals a stored record or appears at a frame boundary inside a
merged payload. The DB map is keyed by `dbHandle`, so a `DBFree`/`DBInit` cycle
(MER's `setIdentifySet`) starts empty. A mutex guards each pipe call and the
DB map.

## Capture protocol

`ZKFPM_AcquireFingerprint` first tries the named pipe `\\.\pipe\wfp-zk-shim`
as a client. It sends `ACQUIRE\n`, then waits at most 2 seconds for a line
`ACQUIRE <idHex> <base64Template>\n` and the `OK\n` acknowledgement. It parses
the hexadecimal id, base64-decodes the template, validates its frame, replies
`OK\n`, and copies the framed template to the caller. The shim cannot originate
the id/template, so it consumes the `ACQUIRE ...` line from the peer; `OK\n` is
accepted whether it arrives from the peer or is sent as the acknowledgement.
A valid `ACQUIRE ...` line is sufficient even without `OK\n`.

If the pipe is unavailable or yields no valid template within 2 seconds, the
shim reads `zkshim-config.txt` next to the DLL. That file holds one line in the
same format:

```
ACQUIRE 0000002a TVpLRkAAAAAAAA...
```

If neither source yields a valid framed template, the call returns nonzero so
MER can poll again. The image buffer is filled only when it is at least
256*256 = 65536 bytes, so a short buffer is never overrun.

Integration note: the simulator GUI hosts the matching pipe endpoint at
`src/ZkShimPipe.cs`. The shim connects as a client, sends `ACQUIRE\n`, then
receives the staged `ACQUIRE <id16> <base64Template>` line and replies `OK`.
Select `ZKTeco shim` in the GUI and click a fingerprint to stage its framed
template. `zkshim-config.txt` remains the unattended fallback used by
`tests/zk_shim_test.py`.

## Evidence

- Signatures, bare-name load, and stdcall binding: local
  `E:/Projects/mer/ZktecoZkfReader.py` lines 106-170 (`ctypes.WinDLL(DLL_NAME)`
  with `DLL_NAME = 'libzkfp.dll'`; `ctypes.wintypes.HANDLE/UINT` arguments).
  The same file's `MAX_TEMPLATE_SIZE = 2048` (line 21) and image sizing via
  `GetCaptureParamsEx` (lines 193-207) set the buffer limits above. MER Python
  is 64-bit, so the x64 DLL is required; the x86 DLL covers 32-bit apps through
   the same KnownDLL registration.
- Toolchain: local WDK 26100 headers under
  `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\{um,shared}`
  and MSVC `v145` under
  `C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231`,
  matching `engine/EngineAdapter.vcxproj`.
- Reference (not fetched; Microsoft Learn unreachable from this environment):
  https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea
  https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-peeknamedpipe
  https://docs.python.org/3/library/ctypes.html#ctypes.WinDLL
