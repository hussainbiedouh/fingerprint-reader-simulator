/*++
    Minimal functional Windows Biometric Framework engine adapter for the
    virtual fingerprint simulator. Matches templates by the 16-byte Id in
    WFP_TEMPLATE_HEADER; captured BIR standard data is the feature set.
--*/

#define WIN32_LEAN_AND_MEAN 1
#include <windows.h>
#include <objbase.h>

#ifndef ARGUMENT_PRESENT
#define ARGUMENT_PRESENT(pointer) ((pointer) != NULL)
#endif

#include <winbio_adapter.h>
#include "..\include\VirtualFingerprintProtocol.h"

// WBS frees adapter-owned output buffers with CoTaskMemFree; internal buffers
// use the same allocator so one free helper covers every path.
static PVOID AllocBytes(SIZE_T size)
{
    PVOID buffer = CoTaskMemAlloc(size);
    if (buffer != NULL) {
        ZeroMemory(buffer, size);
    }
    return buffer;
}

static void FreeBytes(PVOID buffer)
{
    if (buffer != NULL) {
        CoTaskMemFree(buffer);
    }
}

static PUCHAR CopyBytes(const UCHAR *source, SIZE_T size)
{
    if (source == NULL || size == 0) {
        return NULL;
    }

    PUCHAR copy = static_cast<PUCHAR>(AllocBytes(size));
    if (copy != NULL) {
        memcpy(copy, source, size);
    }
    return copy;
}

// WFP_TEMPLATE_HEADER may sit at an unaligned BIR offset; copy before reading.
static BOOLEAN ParseTemplate(
    _In_reads_bytes_(size) const UCHAR *data,
    _In_ SIZE_T size,
    _Out_ UCHAR id[WFP_TEMPLATE_ID_SIZE]
    )
{
    if (data == NULL || size < sizeof(WFP_TEMPLATE_HEADER)) {
        return FALSE;
    }

    WFP_TEMPLATE_HEADER header;
    memcpy(&header, data, sizeof(header));
    if (header.Size < sizeof(WFP_TEMPLATE_HEADER) || header.Size > size) {
        return FALSE;
    }

    memcpy(id, header.Id, WFP_TEMPLATE_ID_SIZE);
    return TRUE;
}

typedef struct _WINIBIO_ENGINE_CONTEXT {
    PUCHAR FeatureSet;
    SIZE_T FeatureSetSize;
    UCHAR FeatureSetId[WFP_TEMPLATE_ID_SIZE];
    BOOLEAN FeatureSetIdValid;

    PUCHAR Enrollment;
    SIZE_T EnrollmentSize;

    PUCHAR Template;
    SIZE_T TemplateSize;
    UCHAR TemplateId[WFP_TEMPLATE_ID_SIZE];
    BOOLEAN TemplateIdValid;
    UCHAR TemplateSubtype;

    UCHAR TemplateGuid[sizeof(GUID)];
    BOOLEAN TemplateGuidValid;
} WINIBIO_ENGINE_CONTEXT, *PWINIBIO_ENGINE_CONTEXT;

static void FreeFeatureSet(PWINIBIO_ENGINE_CONTEXT ctx)
{
    FreeBytes(ctx->FeatureSet);
    ctx->FeatureSet = NULL;
    ctx->FeatureSetSize = 0;
    ctx->FeatureSetIdValid = FALSE;
    ZeroMemory(ctx->FeatureSetId, sizeof(ctx->FeatureSetId));
}

static void FreeEnrollment(PWINIBIO_ENGINE_CONTEXT ctx)
{
    FreeBytes(ctx->Enrollment);
    ctx->Enrollment = NULL;
    ctx->EnrollmentSize = 0;
}

static void FreeTemplate(PWINIBIO_ENGINE_CONTEXT ctx)
{
    FreeBytes(ctx->Template);
    ctx->Template = NULL;
    ctx->TemplateSize = 0;
    ctx->TemplateIdValid = FALSE;
    ctx->TemplateGuidValid = FALSE;
    ZeroMemory(ctx->TemplateId, sizeof(ctx->TemplateId));
    ZeroMemory(ctx->TemplateGuid, sizeof(ctx->TemplateGuid));
}

static PWINIBIO_ENGINE_CONTEXT GetContext(PWINBIO_PIPELINE pipeline)
{
    return (pipeline != NULL)
           ? reinterpret_cast<PWINIBIO_ENGINE_CONTEXT>(pipeline->EngineContext)
           : NULL;
}

// shortcut: equal-Id byte count stands in for match confidence, upgrade to a scored matcher for partial fingerprints
static UCHAR Confidence(const UCHAR left[WFP_TEMPLATE_ID_SIZE], const UCHAR right[WFP_TEMPLATE_ID_SIZE])
{
    UCHAR matching = 0;
    for (SIZE_T i = 0; i < WFP_TEMPLATE_ID_SIZE; i++) {
        if (left[i] == right[i]) {
            matching++;
        }
    }
    return matching;
}

static HRESULT AllocOutput(PUCHAR *buffer, PSIZE_T size, const UCHAR *source, SIZE_T sourceSize)
{
    if (sourceSize == 0) {
        *buffer = NULL;
        *size = 0;
        return S_OK;
    }

    *buffer = CopyBytes(source, sourceSize);
    if (*buffer == NULL) {
        *size = 0;
        return E_OUTOFMEMORY;
    }
    *size = sourceSize;
    return S_OK;
}

static HRESULT EngineAdapterAttach(PWINBIO_PIPELINE pipeline)
{
    if (pipeline == NULL) {
        return E_POINTER;
    }
    if (pipeline->EngineContext != NULL) {
        return S_OK; // Already attached; WBS may retry Attach.
    }

    PWINIBIO_ENGINE_CONTEXT ctx = static_cast<PWINIBIO_ENGINE_CONTEXT>(
        AllocBytes(sizeof(WINIBIO_ENGINE_CONTEXT)));
    if (ctx == NULL) {
        return E_OUTOFMEMORY;
    }

    pipeline->EngineContext = reinterpret_cast<PWINIBIO_ENGINE_CONTEXT>(ctx);
    return S_OK;
}

static HRESULT EngineAdapterDetach(PWINBIO_PIPELINE pipeline)
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL) {
        return S_OK; // Already detached; WBS may retry Detach.
    }

    FreeFeatureSet(ctx);
    FreeEnrollment(ctx);
    FreeTemplate(ctx);
    FreeBytes(ctx);
    pipeline->EngineContext = NULL;
    return S_OK;
}

static HRESULT EngineAdapterClearContext(PWINBIO_PIPELINE pipeline)
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL) {
        return S_OK; // Nothing to clear.
    }

    // Keep the committed template so identification survives a pipeline clear.
    FreeFeatureSet(ctx);
    FreeEnrollment(ctx);
    return S_OK;
}

static HRESULT EngineAdapterQueryPreferredFormat(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_REGISTERED_FORMAT standardFormat,
    PWINBIO_UUID vendorFormat
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    if (standardFormat == NULL || vendorFormat == NULL) {
        return E_POINTER;
    }

    standardFormat->Owner = WINBIO_ANSI_381_FORMAT_OWNER;
    standardFormat->Type = WINBIO_ANSI_381_FORMAT_TYPE;
    ZeroMemory(vendorFormat, sizeof(*vendorFormat));
    return S_OK;
}

static HRESULT EngineAdapterQueryIndexVectorSize(PWINBIO_PIPELINE pipeline, PSIZE_T indexElementCount)
{
    UNREFERENCED_PARAMETER(pipeline);
    if (indexElementCount == NULL) {
        return E_POINTER;
    }
    *indexElementCount = 0;
    return S_OK;
}

static HRESULT EngineAdapterQueryHashAlgorithms(
    PWINBIO_PIPELINE pipeline,
    PSIZE_T algorithmCount,
    PSIZE_T algorithmBufferSize,
    PUCHAR *algorithmBuffer
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    if (algorithmCount == NULL || algorithmBufferSize == NULL || algorithmBuffer == NULL) {
        return E_POINTER;
    }
    *algorithmCount = 0;
    *algorithmBufferSize = 0;
    *algorithmBuffer = NULL;
    return S_OK;
}

static HRESULT EngineAdapterSetHashAlgorithm(
    PWINBIO_PIPELINE pipeline,
    SIZE_T algorithmBufferSize,
    PUCHAR algorithmBuffer
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(algorithmBufferSize);
    UNREFERENCED_PARAMETER(algorithmBuffer);
    return S_OK;
}

static HRESULT EngineAdapterQuerySampleHint(PWINBIO_PIPELINE pipeline, PSIZE_T sampleHint)
{
    UNREFERENCED_PARAMETER(pipeline);
    if (sampleHint == NULL) {
        return E_POINTER;
    }
    *sampleHint = WFP_TEMPLATE_MAX_SIZE;
    return S_OK;
}

// The WBS WBDI sensor adapter wraps the driver capture in a BIR; its
// StandardDataBlock holds the template bytes injected by IOCTL_WFP_SET_TEMPLATE.
static HRESULT EngineAdapterAcceptSampleData(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_BIR sampleBuffer,
    SIZE_T sampleSize,
    WINBIO_BIR_PURPOSE purpose,
    PWINBIO_REJECT_DETAIL rejectDetail
    )
{
    UNREFERENCED_PARAMETER(purpose);
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || rejectDetail == NULL) {
        return E_POINTER;
    }
    *rejectDetail = 0;

    if (sampleBuffer == NULL || sampleSize < sizeof(WINBIO_BIR)) {
        return WINBIO_E_BAD_CAPTURE;
    }

    WINBIO_BIR_DATA block = sampleBuffer->StandardDataBlock;
    if (block.Size == 0 || block.Offset > sampleSize || block.Size > sampleSize - block.Offset) {
        return WINBIO_E_BAD_CAPTURE;
    }
    const UCHAR *data = reinterpret_cast<const UCHAR *>(sampleBuffer) + block.Offset;
    SIZE_T dataSize = block.Size;

    UCHAR id[WFP_TEMPLATE_ID_SIZE];
    if (!ParseTemplate(data, dataSize, id)) {
        return WINBIO_E_BAD_CAPTURE;
    }

    PUCHAR copy = CopyBytes(data, dataSize);
    if (copy == NULL) {
        return E_OUTOFMEMORY;
    }
    FreeFeatureSet(ctx);
    ctx->FeatureSet = copy;
    ctx->FeatureSetSize = dataSize;
    memcpy(ctx->FeatureSetId, id, sizeof(id));
    ctx->FeatureSetIdValid = TRUE;
    return S_OK;
}

static HRESULT EngineAdapterExportEngineData(
    PWINBIO_PIPELINE pipeline,
    WINBIO_BIR_DATA_FLAGS flags,
    PWINBIO_BIR *sampleBuffer,
    PSIZE_T sampleSize
    )
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || sampleBuffer == NULL || sampleSize == NULL) {
        return E_POINTER;
    }
    *sampleBuffer = NULL;
    *sampleSize = 0;

    PUCHAR source = ctx->Enrollment;
    SIZE_T sourceSize = ctx->EnrollmentSize;
    // shortcut: with no committed template, PROCESSED export falls back to enrollment bytes, upgrade when WBS requires a committed template
    if ((flags & WINBIO_DATA_FLAG_PROCESSED) != 0 && ctx->Template != NULL) {
        source = ctx->Template;
        sourceSize = ctx->TemplateSize;
    }
    if (source == NULL) {
        source = ctx->FeatureSet;
        sourceSize = ctx->FeatureSetSize;
    }
    if (source == NULL) {
        return WINBIO_E_NO_CAPTURE_DATA;
    }

    // BIR = descriptors, a real WINBIO_BIR_HEADER block, then the data block.
    SIZE_T headerOffset = sizeof(WINBIO_BIR);
    SIZE_T dataOffset = headerOffset + sizeof(WINBIO_BIR_HEADER);
    SIZE_T birSize = dataOffset + sourceSize;
    WINBIO_BIR *bir = static_cast<WINBIO_BIR *>(AllocBytes(birSize));
    if (bir == NULL) {
        return E_OUTOFMEMORY;
    }

    WINBIO_BIR_HEADER header = {};
    header.ValidFields = WINBIO_BIR_FIELD_BIOMETRIC_TYPE |
                         WINBIO_BIR_FIELD_BIOMETRIC_SUBTYPE |
                         WINBIO_BIR_FIELD_CBEFF_HEADER_VERSION |
                         WINBIO_BIR_FIELD_PATRON_HEADER_VERSION |
                         WINBIO_BIR_FIELD_BIOMETRIC_PURPOSE;
    header.HeaderVersion = WINBIO_CBEFF_HEADER_VERSION;
    header.PatronHeaderVersion = WINBIO_PATRON_HEADER_VERSION;
    header.DataFlags = WINBIO_DATA_FLAG_INTEGRITY |
                       (((flags & WINBIO_DATA_FLAG_PROCESSED) != 0)
                            ? WINBIO_DATA_FLAG_PROCESSED
                            : WINBIO_DATA_FLAG_INTERMEDIATE);
    header.Type = WINBIO_TYPE_FINGERPRINT;
    header.Subtype = ctx->TemplateSubtype;
    header.Purpose = WINBIO_PURPOSE_IDENTIFY;
    header.BiometricDataFormat.Owner = WINBIO_ANSI_381_FORMAT_OWNER;
    header.BiometricDataFormat.Type = WINBIO_ANSI_381_FORMAT_TYPE;

    bir->HeaderBlock.Size = sizeof(WINBIO_BIR_HEADER);
    bir->HeaderBlock.Offset = static_cast<ULONG>(headerOffset);
    bir->StandardDataBlock.Size = static_cast<ULONG>(sourceSize);
    bir->StandardDataBlock.Offset = static_cast<ULONG>(dataOffset);
    memcpy(reinterpret_cast<PUCHAR>(bir) + headerOffset, &header, sizeof(header));
    memcpy(reinterpret_cast<PUCHAR>(bir) + dataOffset, source, sourceSize);

    *sampleBuffer = bir;
    *sampleSize = birSize;
    return S_OK;
}

static HRESULT EngineAdapterCreateEnrollment(PWINBIO_PIPELINE pipeline)
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL) {
        return E_POINTER;
    }
    FreeEnrollment(ctx);
    return S_OK;
}

static HRESULT EngineAdapterUpdateEnrollment(PWINBIO_PIPELINE pipeline, PWINBIO_REJECT_DETAIL rejectDetail)
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || rejectDetail == NULL) {
        return E_POINTER;
    }
    *rejectDetail = 0;
    if (ctx->FeatureSet == NULL) {
        return WINBIO_E_NO_CAPTURE_DATA;
    }

    PUCHAR copy = CopyBytes(ctx->FeatureSet, ctx->FeatureSetSize);
    if (copy == NULL) {
        return E_OUTOFMEMORY;
    }
    FreeEnrollment(ctx);
    ctx->Enrollment = copy;
    ctx->EnrollmentSize = ctx->FeatureSetSize;
    return S_OK;
}

static HRESULT EngineAdapterGetEnrollmentStatus(PWINBIO_PIPELINE pipeline, PWINBIO_REJECT_DETAIL rejectDetail)
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || rejectDetail == NULL) {
        return E_POINTER;
    }
    *rejectDetail = 0;
    return (ctx->Enrollment != NULL) ? S_OK : WINBIO_E_NO_CAPTURE_DATA;
}

static HRESULT EngineAdapterGetEnrollmentHash(
    PWINBIO_PIPELINE pipeline,
    PUCHAR *hashValue,
    PSIZE_T hashSize
    )
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || hashValue == NULL || hashSize == NULL) {
        return E_POINTER;
    }
    *hashValue = NULL;
    *hashSize = 0;
    if (ctx->Enrollment == NULL) {
        return WINBIO_E_NO_CAPTURE_DATA;
    }
    // shortcut: raw enrollment bytes stand in as the hash, upgrade to a real digest when WBS requires cryptographic hashing
    return AllocOutput(hashValue, hashSize, ctx->Enrollment, ctx->EnrollmentSize);
}

static HRESULT EngineAdapterCheckForDuplicate(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_IDENTITY identity,
    PWINBIO_BIOMETRIC_SUBTYPE subFactor,
    PBOOLEAN duplicate
    )
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || identity == NULL || subFactor == NULL || duplicate == NULL) {
        return E_POINTER;
    }

    *duplicate = FALSE;
    ZeroMemory(identity, sizeof(*identity));
    *subFactor = WINBIO_SUBTYPE_NO_INFORMATION;

    if (!ctx->TemplateIdValid || !ctx->FeatureSetIdValid) {
        return S_OK;
    }
    if (Confidence(ctx->FeatureSetId, ctx->TemplateId) != WFP_TEMPLATE_ID_SIZE) {
        return S_OK;
    }

    *duplicate = TRUE;
    identity->Type = WINBIO_ID_TYPE_GUID;
    memcpy(&identity->Value.TemplateGuid, ctx->TemplateGuid, sizeof(GUID));
    *subFactor = ctx->TemplateSubtype;
    return S_OK;
}

static HRESULT EngineAdapterCommitEnrollment(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_IDENTITY identity,
    WINBIO_BIOMETRIC_SUBTYPE subFactor,
    PUCHAR payloadBlob,
    SIZE_T payloadBlobSize
    )
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || identity == NULL) {
        return E_POINTER;
    }

    const PUCHAR source = (payloadBlob != NULL && payloadBlobSize != 0) ? payloadBlob : ctx->Enrollment;
    SIZE_T sourceSize = (payloadBlob != NULL && payloadBlobSize != 0) ? payloadBlobSize : ctx->EnrollmentSize;
    if (source == NULL) {
        return WINBIO_E_NO_CAPTURE_DATA;
    }

    PUCHAR copy = CopyBytes(source, sourceSize);
    if (copy == NULL) {
        return E_OUTOFMEMORY;
    }
    FreeTemplate(ctx);
    ctx->Template = copy;
    ctx->TemplateSize = sourceSize;

    // The committed payload is the enrollment template, so its WFP Id is
    // authoritative.
    ctx->TemplateIdValid = ParseTemplate(source, sourceSize, ctx->TemplateId);
    // shortcut: opaque payloads use their leading 16 bytes as Id, upgrade when the wire format carries an explicit header
    if (!ctx->TemplateIdValid && sourceSize >= WFP_TEMPLATE_ID_SIZE) {
        memcpy(ctx->TemplateId, source, WFP_TEMPLATE_ID_SIZE);
        ctx->TemplateIdValid = TRUE;
    }
    ctx->TemplateSubtype = subFactor;

    if (identity->Type == WINBIO_ID_TYPE_GUID) {
        memcpy(ctx->TemplateGuid, &identity->Value.TemplateGuid, sizeof(GUID));
    } else {
        // shortcut: the template Id doubles as the identity GUID, upgrade when WBS supplies a real account identity
        memcpy(ctx->TemplateGuid, ctx->TemplateId, sizeof(GUID));
    }
    ctx->TemplateGuidValid = TRUE;

    FreeEnrollment(ctx);
    return S_OK;
}

static HRESULT EngineAdapterDiscardEnrollment(PWINBIO_PIPELINE pipeline)
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL) {
        return E_POINTER;
    }
    FreeEnrollment(ctx);
    return S_OK;
}

static HRESULT EngineAdapterVerifyFeatureSet(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_IDENTITY identity,
    WINBIO_BIOMETRIC_SUBTYPE subFactor,
    PBOOLEAN match,
    PUCHAR *payloadBlob,
    PSIZE_T payloadBlobSize,
    PUCHAR *hashValue,
    PSIZE_T hashSize,
    PWINBIO_REJECT_DETAIL rejectDetail
    )
{
    UNREFERENCED_PARAMETER(subFactor);
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || identity == NULL || match == NULL || payloadBlob == NULL ||
        payloadBlobSize == NULL || hashValue == NULL || hashSize == NULL || rejectDetail == NULL) {
        return E_POINTER;
    }

    *match = FALSE;
    *payloadBlob = NULL;
    *payloadBlobSize = 0;
    *hashValue = NULL;
    *hashSize = 0;
    *rejectDetail = 0;

    if (!ctx->TemplateIdValid || !ctx->FeatureSetIdValid) {
        return S_OK;
    }

    // Verify the current capture against the committed template. A GUID
    // identity must be the one that identity was committed under; WBS passes
    // the enrollment GUID here, which is not the template Id.
    BOOLEAN identityMatches = TRUE;
    if (identity->Type == WINBIO_ID_TYPE_GUID) {
        identityMatches = ctx->TemplateGuidValid &&
                          memcmp(&identity->Value.TemplateGuid, ctx->TemplateGuid, sizeof(GUID)) == 0;
    }

    UCHAR confidence = Confidence(ctx->FeatureSetId, ctx->TemplateId);
    *match = identityMatches && (confidence == WFP_TEMPLATE_ID_SIZE);

    HRESULT hr = AllocOutput(hashValue, hashSize, ctx->FeatureSetId, WFP_TEMPLATE_ID_SIZE);
    if (FAILED(hr)) {
        return hr;
    }
    hr = AllocOutput(payloadBlob, payloadBlobSize, &confidence, sizeof(confidence));
    if (FAILED(hr)) {
        FreeBytes(*hashValue);
        *hashValue = NULL;
        *hashSize = 0;
        return hr;
    }
    return S_OK;
}

static HRESULT EngineAdapterIdentifyFeatureSet(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_IDENTITY identity,
    PWINBIO_BIOMETRIC_SUBTYPE subFactor,
    PUCHAR *payloadBlob,
    PSIZE_T payloadBlobSize,
    PUCHAR *hashValue,
    PSIZE_T hashSize,
    PWINBIO_REJECT_DETAIL rejectDetail
    )
{
    PWINIBIO_ENGINE_CONTEXT ctx = GetContext(pipeline);
    if (ctx == NULL || identity == NULL || subFactor == NULL || payloadBlob == NULL ||
        payloadBlobSize == NULL || hashValue == NULL || hashSize == NULL || rejectDetail == NULL) {
        return E_POINTER;
    }

    ZeroMemory(identity, sizeof(*identity));
    *subFactor = WINBIO_SUBTYPE_NO_INFORMATION;
    *payloadBlob = NULL;
    *payloadBlobSize = 0;
    *hashValue = NULL;
    *hashSize = 0;
    *rejectDetail = 0;

    if (!ctx->TemplateIdValid) {
        return WINBIO_E_UNKNOWN_ID;
    }
    if (!ctx->FeatureSetIdValid ||
        Confidence(ctx->FeatureSetId, ctx->TemplateId) != WFP_TEMPLATE_ID_SIZE) {
        return WINBIO_E_NO_MATCH;
    }

    identity->Type = WINBIO_ID_TYPE_GUID;
    memcpy(&identity->Value.TemplateGuid, ctx->TemplateGuid, sizeof(GUID));
    *subFactor = ctx->TemplateSubtype;

    UCHAR confidence = WFP_TEMPLATE_ID_SIZE;
    HRESULT hr = AllocOutput(payloadBlob, payloadBlobSize, &confidence, sizeof(confidence));
    if (FAILED(hr)) {
        return hr;
    }
    hr = AllocOutput(hashValue, hashSize, ctx->TemplateId, WFP_TEMPLATE_ID_SIZE);
    if (FAILED(hr)) {
        FreeBytes(*payloadBlob);
        *payloadBlob = NULL;
        *payloadBlobSize = 0;
        return hr;
    }
    return S_OK;
}

// Control and higher-version callbacks are unused by unit setup/enroll/identify.
static HRESULT Unsupported(void)
{
    return WINBIO_E_INVALID_OPERATION;
}

static HRESULT EngineAdapterControlUnit(
    PWINBIO_PIPELINE pipeline, ULONG controlCode,
    PUCHAR sendBuffer, SIZE_T sendBufferSize,
    PUCHAR receiveBuffer, SIZE_T receiveBufferSize,
    PSIZE_T receiveDataSize, PULONG operationStatus
    )
{
    UNREFERENCED_PARAMETER(pipeline); UNREFERENCED_PARAMETER(controlCode);
    UNREFERENCED_PARAMETER(sendBuffer); UNREFERENCED_PARAMETER(sendBufferSize);
    UNREFERENCED_PARAMETER(receiveBuffer); UNREFERENCED_PARAMETER(receiveBufferSize);
    UNREFERENCED_PARAMETER(receiveDataSize); UNREFERENCED_PARAMETER(operationStatus);
    return Unsupported();
}

static HRESULT EngineAdapterControlUnitPrivileged(
    PWINBIO_PIPELINE pipeline, ULONG controlCode,
    PUCHAR sendBuffer, SIZE_T sendBufferSize,
    PUCHAR receiveBuffer, SIZE_T receiveBufferSize,
    PSIZE_T receiveDataSize, PULONG operationStatus
    )
{
    UNREFERENCED_PARAMETER(pipeline); UNREFERENCED_PARAMETER(controlCode);
    UNREFERENCED_PARAMETER(sendBuffer); UNREFERENCED_PARAMETER(sendBufferSize);
    UNREFERENCED_PARAMETER(receiveBuffer); UNREFERENCED_PARAMETER(receiveBufferSize);
    UNREFERENCED_PARAMETER(receiveDataSize); UNREFERENCED_PARAMETER(operationStatus);
    return Unsupported();
}

// V2-V4 callbacks exposed by the V5 interface table. They are no-ops or
// explicit "unsupported" results; none may dereference uninitialized outputs.
static HRESULT EngineAdapterNoOp(PWINBIO_PIPELINE pipeline)
{
    UNREFERENCED_PARAMETER(pipeline);
    return S_OK;
}

static HRESULT EngineAdapterNotifyPowerChange(PWINBIO_PIPELINE pipeline, ULONG powerEventType)
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(powerEventType);
    return S_OK;
}

static HRESULT EngineAdapterReserved1(PWINBIO_PIPELINE pipeline, PWINBIO_IDENTITY identity)
{
    UNREFERENCED_PARAMETER(pipeline);
    if (identity == NULL) {
        return E_POINTER;
    }
    ZeroMemory(identity, sizeof(*identity));
    return S_OK;
}

static HRESULT EngineAdapterPipelineInit(PWINBIO_PIPELINE pipeline)
{
    return EngineAdapterNoOp(pipeline);
}

static HRESULT EngineAdapterPipelineCleanup(PWINBIO_PIPELINE pipeline)
{
    return EngineAdapterNoOp(pipeline);
}

static HRESULT EngineAdapterActivate(PWINBIO_PIPELINE pipeline)
{
    return EngineAdapterNoOp(pipeline);
}

static HRESULT EngineAdapterDeactivate(PWINBIO_PIPELINE pipeline)
{
    return EngineAdapterNoOp(pipeline);
}

static HRESULT EngineAdapterQueryExtendedInfo(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_EXTENDED_ENGINE_INFO engineInfo,
    SIZE_T engineInfoSize
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    if (engineInfo == NULL) {
        return E_POINTER;
    }
    ZeroMemory(engineInfo, min(engineInfoSize, sizeof(*engineInfo)));
    return S_OK;
}

static HRESULT EngineAdapterIdentifyAll(
    PWINBIO_PIPELINE pipeline,
    PSIZE_T presenceCount,
    PWINBIO_PRESENCE *presenceArray
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    if (presenceCount == NULL || presenceArray == NULL) {
        return E_POINTER;
    }
    *presenceCount = 0;
    *presenceArray = NULL;
    return S_OK;
}

static HRESULT EngineAdapterSetEnrollmentSelector(PWINBIO_PIPELINE pipeline, ULONGLONG selectorValue)
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(selectorValue);
    return S_OK;
}

static HRESULT EngineAdapterSetEnrollmentParameters(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_EXTENDED_ENROLLMENT_PARAMETERS parameters
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(parameters);
    return S_OK;
}

static HRESULT EngineAdapterQueryExtendedEnrollmentStatus(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_EXTENDED_ENROLLMENT_STATUS status,
    SIZE_T statusSize
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    if (status == NULL) {
        return E_POINTER;
    }
    ZeroMemory(status, min(statusSize, sizeof(*status)));
    return S_OK;
}

static HRESULT EngineAdapterRefreshCache(PWINBIO_PIPELINE pipeline)
{
    return EngineAdapterNoOp(pipeline);
}

static HRESULT EngineAdapterSelectCalibrationFormat(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_UUID formatArray, SIZE_T formatCount,
    PWINBIO_UUID selectedFormat, PSIZE_T maxBufferSize
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(formatArray);
    UNREFERENCED_PARAMETER(formatCount);
    UNREFERENCED_PARAMETER(selectedFormat);
    UNREFERENCED_PARAMETER(maxBufferSize);
    // Opt out of dynamic calibration; WBS converts E_NOTIMPL to S_OK.
    return E_NOTIMPL;
}

static HRESULT EngineAdapterQueryCalibrationData(
    PWINBIO_PIPELINE pipeline,
    PBOOLEAN discardAndRepeatCapture,
    PUCHAR calibrationBuffer, PSIZE_T calibrationBufferSize,
    SIZE_T maxBufferSize
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(calibrationBuffer);
    UNREFERENCED_PARAMETER(maxBufferSize);
    if (discardAndRepeatCapture == NULL || calibrationBufferSize == NULL) {
        return E_POINTER;
    }
    *discardAndRepeatCapture = FALSE;
    *calibrationBufferSize = 0;
    return S_OK;
}

static HRESULT EngineAdapterSetAccountPolicy(
    PWINBIO_PIPELINE pipeline,
    PWINBIO_ACCOUNT_POLICY policyItemArray, SIZE_T policyItemCount
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(policyItemArray);
    UNREFERENCED_PARAMETER(policyItemCount);
    return S_OK;
}

static HRESULT EngineAdapterCreateKey(
    PWINBIO_PIPELINE pipeline,
    const UCHAR *key, SIZE_T keySize,
    PUCHAR keyIdentifier, SIZE_T keyIdentifierSize,
    PSIZE_T resultSize
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(key);
    UNREFERENCED_PARAMETER(keySize);
    UNREFERENCED_PARAMETER(keyIdentifier);
    UNREFERENCED_PARAMETER(keyIdentifierSize);
    if (resultSize == NULL) {
        return E_POINTER;
    }
    *resultSize = 0;
    return Unsupported();
}

static HRESULT EngineAdapterIdentifyFeatureSetSecure(
    PWINBIO_PIPELINE pipeline,
    const UCHAR *nonce, SIZE_T nonceSize,
    const UCHAR *keyIdentifier, SIZE_T keyIdentifierSize,
    PWINBIO_IDENTITY identity, PWINBIO_BIOMETRIC_SUBTYPE subFactor,
    PWINBIO_REJECT_DETAIL rejectDetail,
    PUCHAR *authorization, PSIZE_T authorizationSize
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(nonce);
    UNREFERENCED_PARAMETER(nonceSize);
    UNREFERENCED_PARAMETER(keyIdentifier);
    UNREFERENCED_PARAMETER(keyIdentifierSize);
    if (identity != NULL) {
        ZeroMemory(identity, sizeof(*identity));
    }
    if (subFactor != NULL) {
        *subFactor = WINBIO_SUBTYPE_NO_INFORMATION;
    }
    if (rejectDetail != NULL) {
        *rejectDetail = 0;
    }
    if (authorization != NULL) {
        *authorization = NULL;
    }
    if (authorizationSize != NULL) {
        *authorizationSize = 0;
    }
    return Unsupported();
}

// V5.0: WBS strips the IOCTL envelope, so this is the raw private type blob
// (the 20-byte WFP_TEMPLATE_HEADER), not a WINBIO_PRIVATE_SENSOR_TYPE_INFO.
static HRESULT EngineAdapterAcceptPrivateSensorTypeInfo(
    PWINBIO_PIPELINE pipeline,
    const UCHAR *typeInfoBufferAddress,
    SIZE_T typeInfoBufferSize
    )
{
    UNREFERENCED_PARAMETER(pipeline);
    UNREFERENCED_PARAMETER(typeInfoBufferSize);
    if (typeInfoBufferAddress == NULL) {
        return E_INVALIDARG;
    }
    return S_OK;
}

static WINBIO_ENGINE_INTERFACE MakeInterface()
{
    WINBIO_ENGINE_INTERFACE iface = {};
    iface.Version = WINBIO_ENGINE_INTERFACE_VERSION_5;
    iface.Type = WINBIO_ADAPTER_TYPE_ENGINE;
    iface.Size = sizeof(WINBIO_ENGINE_INTERFACE);
    iface.AdapterId = {0x1f4e2c3a, 0x5b6d, 0x4e7f, {0x8a, 0x9b, 0x0c, 0x1d, 0x2e, 0x3f, 0x4a, 0x5b}};

    iface.Attach = EngineAdapterAttach;
    iface.Detach = EngineAdapterDetach;
    iface.ClearContext = EngineAdapterClearContext;
    iface.QueryPreferredFormat = EngineAdapterQueryPreferredFormat;
    iface.QueryIndexVectorSize = EngineAdapterQueryIndexVectorSize;
    iface.QueryHashAlgorithms = EngineAdapterQueryHashAlgorithms;
    iface.SetHashAlgorithm = EngineAdapterSetHashAlgorithm;
    iface.QuerySampleHint = EngineAdapterQuerySampleHint;
    iface.AcceptSampleData = EngineAdapterAcceptSampleData;
    iface.ExportEngineData = EngineAdapterExportEngineData;
    iface.VerifyFeatureSet = EngineAdapterVerifyFeatureSet;
    iface.IdentifyFeatureSet = EngineAdapterIdentifyFeatureSet;
    iface.CreateEnrollment = EngineAdapterCreateEnrollment;
    iface.UpdateEnrollment = EngineAdapterUpdateEnrollment;
    iface.GetEnrollmentStatus = EngineAdapterGetEnrollmentStatus;
    iface.GetEnrollmentHash = EngineAdapterGetEnrollmentHash;
    iface.CheckForDuplicate = EngineAdapterCheckForDuplicate;
    iface.CommitEnrollment = EngineAdapterCommitEnrollment;
    iface.DiscardEnrollment = EngineAdapterDiscardEnrollment;
    iface.ControlUnit = EngineAdapterControlUnit;
    iface.ControlUnitPrivileged = EngineAdapterControlUnitPrivileged;

    // V2.0
    iface.NotifyPowerChange = EngineAdapterNotifyPowerChange;
    iface.Reserved_1 = EngineAdapterReserved1;

    // V3.0
    iface.PipelineInit = EngineAdapterPipelineInit;
    iface.PipelineCleanup = EngineAdapterPipelineCleanup;
    iface.Activate = EngineAdapterActivate;
    iface.Deactivate = EngineAdapterDeactivate;
    iface.QueryExtendedInfo = EngineAdapterQueryExtendedInfo;
    iface.IdentifyAll = EngineAdapterIdentifyAll;
    iface.SetEnrollmentSelector = EngineAdapterSetEnrollmentSelector;
    iface.SetEnrollmentParameters = EngineAdapterSetEnrollmentParameters;
    iface.QueryExtendedEnrollmentStatus = EngineAdapterQueryExtendedEnrollmentStatus;
    iface.RefreshCache = EngineAdapterRefreshCache;
    iface.SelectCalibrationFormat = EngineAdapterSelectCalibrationFormat;
    iface.QueryCalibrationData = EngineAdapterQueryCalibrationData;
    iface.SetAccountPolicy = EngineAdapterSetAccountPolicy;

    // V4.0
    iface.CreateKey = EngineAdapterCreateKey;
    iface.IdentifyFeatureSetSecure = EngineAdapterIdentifyFeatureSetSecure;

    // V5.0
    iface.AcceptPrivateSensorTypeInfo = EngineAdapterAcceptPrivateSensorTypeInfo;
    return iface;
}

static PWINBIO_ENGINE_INTERFACE EngineInterface()
{
    static WINBIO_ENGINE_INTERFACE iface = MakeInterface();
    return &iface;
}

BOOL APIENTRY DllMain(HANDLE module, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(module);
    UNREFERENCED_PARAMETER(reason);
    UNREFERENCED_PARAMETER(reserved);
    return TRUE;
}

// Required by WBS: winbio_adapter.h says this export MUST be named
// WbioQueryEngineInterface (WINBIO_QUERY_ENGINE_INTERFACE_FN_NAME).
extern "C" HRESULT WINAPI WbioQueryEngineInterface(PWINBIO_ENGINE_INTERFACE *engineInterface)
{
    if (engineInterface == NULL) {
        return E_POINTER;
    }
    *engineInterface = EngineInterface();
    return S_OK;
}

// Alias for the name requested by the simulator spec; WBS itself uses the
// WbioQueryEngineInterface export above.
extern "C" HRESULT WINAPI WbioAdapterInterface(PWINBIO_ENGINE_INTERFACE *engineInterface)
{
    return WbioQueryEngineInterface(engineInterface);
}

// EndOperation belongs to the storage adapter interface, not the engine
// interface. Provided as a no-op so the simulator spec's call site is safe.
extern "C" HRESULT WINAPI EndOperation(PWINBIO_PIPELINE pipeline)
{
    UNREFERENCED_PARAMETER(pipeline);
    return S_OK;
}
