# Engine Adapter EngineAdapter.DLL

Minimal functional WBS engine adapter for the virtual fingerprint simulator.
Built with VS18 (v145) + WDK 26100, x64, `Release`.

## Build

```
MSBuild.exe engine\EngineAdapter.sln /p:Configuration=Release /p:Platform=x64
```

Output: `engine\x64\Release\EngineAdapter.dll`. Install as
`%windir%\System32\WinBioPlugins\EngineAdapter.DLL` (the driver INF sets
`EngineAdapterBinary=EngineAdapter.DLL`).

## Exports

- `WbioQueryEngineInterface` — required by WBS; `winbio_adapter.h` says this
  name is mandatory (`WINBIO_QUERY_ENGINE_INTERFACE_FN_NAME`).
- `WbioAdapterInterface` — alias with the same signature, for the name used in
  the simulator spec. `WINBIO_ADAPTER_INTERFACE` has no such symbol; WBS does
  not call it.
- `EndOperation` — no-op. `EndOperation` is a **storage** adapter callback and
  is not a member of `WINBIO_ENGINE_INTERFACE`; provided only for spec callers.

## Behavior

Interface: V5.0 (`WINBIO_ENGINE_INTERFACE_VERSION_5`),
`WINBIO_ADAPTER_TYPE_ENGINE`, a private adapter GUID. V5 is the minimum version
that declares `AcceptPrivateSensorTypeInfo` (`winbio_adapter.h:1681-1691`),
which WBS calls after the sensor answers
`IOCTL_BIOMETRIC_GET_PRIVATE_SENSOR_TYPE`. WBS passes the raw blob with the IOCTL
envelope stripped; the adapter only checks for a non-null pointer, so the
20-byte WFP header blob is accepted. `SelectCalibrationFormat` returns
`E_NOTIMPL` to opt out of dynamic calibration (WBS turns it into `S_OK`).
V2-V4 fields are populated with lifecycle no-ops or explicit unsupported
results. `QueryPreferredFormat` returns ANSI-381
(`Owner=WINBIO_ANSI_381_FORMAT_OWNER`, `Type=WINBIO_ANSI_381_FORMAT_TYPE`).

The captured BIR's `StandardDataBlock` is the feature set. Its first bytes are
a `WFP_TEMPLATE_HEADER` (`Size`, 16-byte `Id`) followed by opaque data. The
16-byte `Id` is the match key. `UpdateEnrollment` copies the feature set into
the in-progress enrollment; `GetEnrollmentHash` returns those bytes as the
hash; `CommitEnrollment` stores the payload WBS returns (or the enrollment
bytes) as the committed template in adapter context. `IdentifyFeatureSet`
returns the committed identity GUID (the template Id for non-GUID commits),
subtype from the committed `SubFactor`, and a 1-byte confidence (16 = full Id
match) in the payload;
returns `WINBIO_E_UNKNOWN_ID` when no template is committed and
`WINBIO_E_NO_MATCH` on an actual capture mismatch. `Attach`/`Detach`/
`ClearContext` are idempotent. `VerifyFeatureSet` matches the **current
capture** against the committed template Id and requires a supplied GUID
identity to be the one committed with that template; non-match is `S_OK` with
`Match=FALSE`. `ExportEngineData` emits a BIR with a real `WINBIO_BIR_HEADER`
block plus the data block. `ClearContext` keeps the committed template. All
allocations are released on `Detach`; transient buffers on
`ClearContext`/`DiscardEnrollment`. All buffers use `CoTaskMemAlloc`/`Free`,
matching how WBS frees adapter-owned outputs. `NTDDI_VERSION` is pinned to
`0x0A000010` so `sizeof(WINBIO_ENGINE_INTERFACE)` stays 360 bytes.

Assumption: the BIR standard data block begins with `WFP_TEMPLATE_HEADER`.
The WBDI driver stores the bytes *after* the outer injection header, so the
injector must place a full `WFP_TEMPLATE_HEADER` at the start of those bytes.
If the payload is opaque and at least 16 bytes, the leading 16 bytes are used
as the Id.

## Implemented

`Attach`, `Detach`, `ClearContext`, `QueryPreferredFormat`,
`QueryIndexVectorSize` (0 indices), `QueryHashAlgorithms` (0 algorithms),
`SetHashAlgorithm` (accept/no-op), `QuerySampleHint`, `AcceptSampleData`,
`ExportEngineData`, `CreateEnrollment`, `UpdateEnrollment`,
`GetEnrollmentStatus`, `GetEnrollmentHash`, `CheckForDuplicate`,
`CommitEnrollment`, `DiscardEnrollment`, `VerifyFeatureSet`,
`IdentifyFeatureSet`, `AcceptPrivateSensorTypeInfo`.

V2-V4 callbacks are populated: `NotifyPowerChange`, `Reserved_1`,
`PipelineInit`, `PipelineCleanup`, `Activate`, `Deactivate`,
`QueryExtendedInfo`, `IdentifyAll`, `SetEnrollmentSelector`,
`SetEnrollmentParameters`, `QueryExtendedEnrollmentStatus`, `RefreshCache`,
`QueryCalibrationData`, `SetAccountPolicy` are lifecycle no-ops / zero-output
handlers returning `S_OK`. `SelectCalibrationFormat` returns `E_NOTIMPL`.

## Unsupported (`WINBIO_E_INVALID_OPERATION`)

`ControlUnit`, `ControlUnitPrivileged` — no private/vendor control codes are
used by setup, enrollment or identification in this simulator. V4 callbacks
`CreateKey` and `IdentifyFeatureSetSecure` write zeroed outputs and return
unsupported. V6 callbacks (`CreateEnrollmentAuthenticated`,
`IdentifyFeatureSetAuthenticated`) are NULL — the adapter declares V5.0, so
WBS does not call them.

Note: `WINBIO_E_UNSUPPORTED_OPERATION` does not exist in WDK 26100
`winbio_err.h`; `WINBIO_E_INVALID_OPERATION` (`0x8009802C`) is the real closest
code.

## Verification / references

Built with 0 errors; `dumpbin /exports` shows all three exports. A local
LoadLibrary self-check exercised attach, format query, sample acceptance,
enroll → commit → identify (GUID/subtype/confidence), verify match and
non-match, no-template no-match, unsupported control, and detach — all passed.

Signatures and return codes checked against local
`C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\winbio_adapter.h`,
`...\shared\winbio_types.h`, `...\shared\winbio_err.h`. Microsoft Learn was
unreachable from this environment (DNS failure), so the URLs below are for
reference and were not fetched:

- Engine adapter reference: https://learn.microsoft.com/en-us/windows/win32/api/winbio_adapter/
- `WbioQueryEngineInterface`: https://learn.microsoft.com/en-us/windows/win32/api/winbio_adapter/nf-winbio_adapter-wbioqueryengineinterface
- `WINBIO_ENGINE_INTERFACE`: https://learn.microsoft.com/en-us/windows/win32/api/winbio_adapter/ns-winbio_adapter-winbio_engine_interface
- `AcceptSampleData`: https://learn.microsoft.com/en-us/windows/win32/api/winbio_adapter/nc-winbio_adapter-pibio_engine_accept_sample_data_fn
- `IdentifyFeatureSet`: https://learn.microsoft.com/en-us/windows/win32/api/winbio_adapter/nc-winbio_adapter-pibio_engine_identify_feature_set_fn
- `VerifyFeatureSet`: https://learn.microsoft.com/en-us/windows/win32/api/winbio_adapter/nc-winbio_adapter-pibio_engine_verify_feature_set_fn
- `WINBIO_IDENTITY`: https://learn.microsoft.com/en-us/windows/win32/api/winbio_types/ns-winbio_types-winbio_identity
- `WINBIO_REGISTERED_FORMAT`: https://learn.microsoft.com/en-us/windows/win32/api/winbio_types/ns-winbio_types-winbio_registered_format
