#include <windows.h>
#include <wdf.h>
#include <winbio_ioctl.h>
#include <winbio_err.h>
#include "Driver.h"

static VOID
WfpCopyWinBioString(
    _Out_ WINBIO_STRING Destination,
    _In_ PCWSTR Source
    )
{
    ULONG index = 0;
    while (Source[index] != L'\0' && index < WINBIO_MAX_STRING_LEN - 1) {
        Destination[index] = Source[index];
        index++;
    }
    Destination[index] = L'\0';
}

static VOID
WfpCompleteWithPayload(
    _In_ WDFREQUEST Request,
    _In_ PVOID Payload,
    _In_ ULONG PayloadSize
    )
{
    PVOID output;
    size_t outputLength;
    NTSTATUS status;

    status = WdfRequestRetrieveOutputBuffer(Request, PayloadSize, &output, &outputLength);
    if (!NT_SUCCESS(status)) {
        WdfRequestSetInformation(Request, PayloadSize);
        WdfRequestComplete(Request, status);
        return;
    }

    RtlCopyMemory(output, Payload, PayloadSize);
    WdfRequestSetInformation(Request, PayloadSize);
    WdfRequestComplete(Request, STATUS_SUCCESS);
}

static ULONG
WfpCapturePayloadSize(
    _In_ ULONG TemplateSize
    )
{
    return FIELD_OFFSET(WINBIO_CAPTURE_DATA, CaptureData.Data) + TemplateSize;
}

// Caller holds WfpStateLock.
static VOID
WfpWriteCapturePayload(
    _In_ WDFREQUEST Request
    )
{
    PWINBIO_CAPTURE_DATA capture;
    PVOID output;
    size_t outputLength;
    ULONG payloadSize;
    ULONG templateSize;

    if (!NT_SUCCESS(WdfRequestRetrieveOutputBuffer(
                        Request,
                        FIELD_OFFSET(WINBIO_CAPTURE_DATA, CaptureData.Data),
                        &output,
                        &outputLength))) {
        return;
    }

    capture = (PWINBIO_CAPTURE_DATA)output;

    templateSize = WfpCaptureTemplateValid ? WfpCaptureTemplateSize : 0;
    payloadSize = WfpCapturePayloadSize(templateSize);
    if (outputLength >= payloadSize) {
        RtlZeroMemory(capture, FIELD_OFFSET(WINBIO_CAPTURE_DATA, CaptureData.Data));
        capture->PayloadSize = payloadSize;
        capture->WinBioHresult = WfpCaptureTemplateValid ? S_OK : WINBIO_E_NO_CAPTURE_DATA;
        capture->SensorStatus = WfpCaptureTemplateValid ? WINBIO_SENSOR_ACCEPT
                                                       : WINBIO_SENSOR_STATUS_UNKNOWN;
        capture->RejectDetail = 0;
        capture->CaptureData.Size = templateSize;
        if (templateSize != 0) {
            RtlCopyMemory(capture->CaptureData.Data, WfpCaptureTemplate, templateSize);
        }
    }

    WdfRequestSetInformation(Request, payloadSize);
}

static NTSTATUS
WfpFinishPendingCapture(
    _In_ NTSTATUS CompletionStatus,
    _In_ BOOLEAN WritePayload
    )
{
    WDFREQUEST request;
    NTSTATUS status;

    WdfWaitLockAcquire(WfpStateLock, NULL);
    request = WfpPendingCapture;
    if (request == NULL) {
        WdfWaitLockRelease(WfpStateLock);
        return STATUS_SUCCESS;
    }

    // Unmark under the lock: the cancel callback takes the same lock, so the
    // request is completed by exactly one path.
    status = WdfRequestUnmarkCancelable(request);
    if (status != STATUS_CANCELLED) {
        if (WritePayload && NT_SUCCESS(CompletionStatus)) {
            WfpWriteCapturePayload(request);
        }
        WdfRequestComplete(request, CompletionStatus);
    }
    WfpPendingCapture = NULL;
    WdfWaitLockRelease(WfpStateLock);
    return status;
}

NTSTATUS
WfpCompletePendingCapture(
    _In_ NTSTATUS CompletionStatus
    )
{
    return WfpFinishPendingCapture(CompletionStatus, FALSE);
}

NTSTATUS
WfpTriggerPendingCapture(
    VOID
    )
{
    NTSTATUS status = WfpFinishPendingCapture(STATUS_SUCCESS, TRUE);

    // A canceled capture is not a failure of the trigger request itself.
    return status == STATUS_CANCELLED ? STATUS_SUCCESS : status;
}

// Caller holds WfpStateLock.
static VOID
WfpRequestCompleteLocked(
    _In_ WDFREQUEST Request,
    _In_ NTSTATUS Status
    )
{
    if (WfpPendingCapture == Request) {
        WfpPendingCapture = NULL;
    }
    WdfRequestComplete(Request, Status);
}

VOID
NTAPI
WfpEvtRequestCancel(
    _In_ WDFREQUEST Request
    )
{
    WdfWaitLockAcquire(WfpStateLock, NULL);
    WfpRequestCompleteLocked(Request, STATUS_CANCELLED);
    WdfWaitLockRelease(WfpStateLock);
}

VOID
NTAPI
WfpEvtIoStop(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ ULONG ActionFlags
    )
{
    WDFREQUEST pending;

    UNREFERENCED_PARAMETER(Queue);

    WdfWaitLockAcquire(WfpStateLock, NULL);
    pending = WfpPendingCapture;
    WdfWaitLockRelease(WfpStateLock);

    if (pending == Request) {
        WfpCompletePendingCapture(STATUS_CANCELLED);
        return;
    }

    if (ActionFlags & WdfRequestStopActionSuspend) {
        WdfRequestStopAcknowledge(Request, TRUE);
    } else {
        WdfRequestStopAcknowledge(Request, FALSE);
    }
}

static VOID
WfpOnGetAttributes(
    _In_ WDFREQUEST Request
    )
{
    WINBIO_SENSOR_ATTRIBUTES attributes;

    RtlZeroMemory(&attributes, sizeof(attributes));
    attributes.PayloadSize = sizeof(attributes);
    attributes.WinBioHresult = S_OK;
    attributes.WinBioVersion.MajorVersion = WINBIO_WBDI_MAJOR_VERSION;
    attributes.WinBioVersion.MinorVersion = WINBIO_WBDI_MINOR_VERSION;
    attributes.SensorType = WINBIO_TYPE_FINGERPRINT;
    attributes.SensorSubType = WINBIO_FP_SENSOR_SUBTYPE_TOUCH;
    attributes.Capabilities = WINBIO_CAPABILITY_SENSOR | WINBIO_CAPABILITY_VIRTUAL_SENSOR;
    WfpCopyWinBioString(attributes.ManufacturerName, L"Windows Fingerprint Simulator");
    WfpCopyWinBioString(attributes.ModelName, L"Virtual WBDI Sensor");
    WfpCopyWinBioString(attributes.SerialNumber, L"WFP-0001");
    attributes.FirmwareVersion.MajorVersion = 1;
    attributes.FirmwareVersion.MinorVersion = 0;
    attributes.SupportedFormatEntries = 1;
    attributes.SupportedFormat[0].Owner = WINBIO_ANSI_381_FORMAT_OWNER;
    attributes.SupportedFormat[0].Type = WINBIO_ANSI_381_FORMAT_TYPE;

    WfpCompleteWithPayload(Request, &attributes, sizeof(attributes));
}

static VOID
WfpOnReset(
    _In_ WDFREQUEST Request
    )
{
    WINBIO_BLANK_PAYLOAD payload;

    RtlZeroMemory(&payload, sizeof(payload));
    payload.PayloadSize = sizeof(payload);
    payload.WinBioHresult = S_OK;
    WfpCompleteWithPayload(Request, &payload, sizeof(payload));
}

static VOID
WfpOnCalibrate(
    _In_ WDFREQUEST Request
    )
{
    WINBIO_CALIBRATION_INFO info;

    RtlZeroMemory(&info, sizeof(info));
    info.PayloadSize = sizeof(info);
    info.WinBioHresult = S_OK;
    info.CalibrationData.Size = 0;
    WfpCompleteWithPayload(Request, &info, sizeof(info));
}

static VOID
WfpOnGetSensorStatus(
    _In_ WDFREQUEST Request
    )
{
    WINBIO_DIAGNOSTICS diagnostics;

    RtlZeroMemory(&diagnostics, sizeof(diagnostics));
    diagnostics.PayloadSize = sizeof(diagnostics);
    diagnostics.WinBioHresult = S_OK;
    diagnostics.SensorStatus = WINBIO_SENSOR_READY;
    diagnostics.VendorDiagnostics.Size = 0;
    WfpCompleteWithPayload(Request, &diagnostics, sizeof(diagnostics));
}

static VOID
WfpOnCaptureData(
    _In_ WDFREQUEST Request
    )
{
    PVOID output;
    size_t outputLength;
    ULONG templateSize;
    ULONG payloadSize;
    NTSTATUS status;

    status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), &output, &outputLength);
    if (!NT_SUCCESS(status)) {
        WdfRequestSetInformation(Request, sizeof(ULONG));
        WdfRequestComplete(Request, status);
        return;
    }

    if (outputLength == sizeof(ULONG)) {
        WdfWaitLockAcquire(WfpStateLock, NULL);
        templateSize = WfpTemplateValid ? WfpTemplateSize : 0;
        WdfWaitLockRelease(WfpStateLock);
        *(PULONG)output = WfpCapturePayloadSize(templateSize);
        WdfRequestSetInformation(Request, sizeof(ULONG));
        WdfRequestComplete(Request, STATUS_SUCCESS);
        return;
    }

    WdfWaitLockAcquire(WfpStateLock, NULL);
    if (WfpPendingCapture != NULL) {
        WdfWaitLockRelease(WfpStateLock);
        WdfRequestComplete(Request, (NTSTATUS)WINBIO_E_DATA_COLLECTION_IN_PROGRESS);
        return;
    }

    // Freeze the template for this capture. A later SET_TEMPLATE updates the
    // next capture without changing the size validated here.
    WfpCaptureTemplateValid = WfpTemplateValid;
    WfpCaptureTemplateSize = WfpTemplateSize;
    if (WfpTemplateSize != 0) {
        RtlCopyMemory(WfpCaptureTemplate, WfpTemplate, WfpTemplateSize);
    }
    WdfWaitLockRelease(WfpStateLock);

    payloadSize = WfpCapturePayloadSize(WfpCaptureTemplateSize);
    if (outputLength < payloadSize) {
        WdfRequestSetInformation(Request, payloadSize);
        WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
        return;
    }

    // Publish only after cancelability is established. Holding the lock keeps
    // the cancel callback from completing the request before it is published.
    WdfWaitLockAcquire(WfpStateLock, NULL);
    if (WfpPendingCapture != NULL) {
        WdfWaitLockRelease(WfpStateLock);
        WdfRequestComplete(Request, (NTSTATUS)WINBIO_E_DATA_COLLECTION_IN_PROGRESS);
        return;
    }
    status = WdfRequestMarkCancelableEx(Request, WfpEvtRequestCancel);
    if (status == STATUS_SUCCESS) {
        WfpPendingCapture = Request;
    }
    WdfWaitLockRelease(WfpStateLock);

    if (status == STATUS_CANCELLED) {
        return;
    }
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
    }
}

static VOID
WfpOnGetPrivateSensorType(
    _In_ WDFREQUEST Request
    )
{
    struct {
        WINBIO_PRIVATE_SENSOR_TYPE_INFO Info;
        UCHAR Data[sizeof(WFP_TEMPLATE_HEADER) - 1];
    } payload;
    ULONG payloadSize;

    static_assert(IOCTL_BIOMETRIC_GET_PRIVATE_SENSOR_TYPE == 0x00440028,
                  "Unexpected WBDI private sensor type IOCTL code");

    payloadSize = FIELD_OFFSET(WINBIO_PRIVATE_SENSOR_TYPE_INFO, PrivateSensorTypeInfo.Data) +
                  sizeof(WFP_TEMPLATE_HEADER);
    RtlZeroMemory(&payload, payloadSize);
    payload.Info.PayloadSize = payloadSize;
    payload.Info.WinBioHresult = S_OK;
    payload.Info.PrivateSensorTypeInfo.Size = sizeof(WFP_TEMPLATE_HEADER);
    WfpCompleteWithPayload(Request, &payload, payloadSize);
}

static VOID
WfpDispatchSensorIoctl(
    _In_ WDFREQUEST Request,
    _In_ ULONG IoControlCode
    )
{
    switch (IoControlCode) {
    case IOCTL_BIOMETRIC_GET_ATTRIBUTES:
        WfpOnGetAttributes(Request);
        break;
    case IOCTL_BIOMETRIC_RESET:
        WfpOnReset(Request);
        break;
    case IOCTL_BIOMETRIC_CALIBRATE:
        WfpOnCalibrate(Request);
        break;
    case IOCTL_BIOMETRIC_GET_SENSOR_STATUS:
        WfpOnGetSensorStatus(Request);
        break;
    case IOCTL_BIOMETRIC_CAPTURE_DATA:
        WfpOnCaptureData(Request);
        break;
    case IOCTL_BIOMETRIC_GET_PRIVATE_SENSOR_TYPE:
        WfpOnGetPrivateSensorType(Request);
        break;
    default:
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;
    }
}

static VOID
WfpOnSetTemplate(
    _In_ WDFREQUEST Request
    )
{
    PVOID input;
    size_t inputLength;
    PWFP_TEMPLATE_HEADER header;
    NTSTATUS status;

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(WFP_TEMPLATE_HEADER), &input, &inputLength);
    if (!NT_SUCCESS(status)) {
        WdfRequestSetInformation(Request, sizeof(WFP_TEMPLATE_HEADER));
        WdfRequestComplete(Request, status);
        return;
    }

    header = (PWFP_TEMPLATE_HEADER)input;
    if (header->Size < sizeof(WFP_TEMPLATE_HEADER) ||
        header->Size > WFP_TEMPLATE_MAX_SIZE ||
        header->Size > inputLength) {
        WdfRequestComplete(Request, STATUS_INVALID_PARAMETER);
        return;
    }

    WdfWaitLockAcquire(WfpStateLock, NULL);
    // Store the full buffer: the engine adapter expects the 20-byte
    // WFP_TEMPLATE_HEADER at the start of CaptureData.Data.
    RtlCopyMemory(WfpTemplate, input, header->Size);
    WfpTemplateSize = header->Size;
    WfpTemplateValid = TRUE;
    WdfWaitLockRelease(WfpStateLock);

    WdfRequestSetInformation(Request, 0);
    WdfRequestComplete(Request, STATUS_SUCCESS);
}

static VOID
WfpDispatchControlIoctl(
    _In_ WDFREQUEST Request,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status;

    switch (IoControlCode) {
    case IOCTL_WFP_SET_TEMPLATE:
        WfpOnSetTemplate(Request);
        break;
    case IOCTL_WFP_TRIGGER_READ:
        status = WfpTriggerPendingCapture();
        WdfRequestComplete(Request, status);
        break;
    default:
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;
    }
}

VOID
NTAPI
WfpEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    WFP_DEVICE_CONTEXT *context;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    context = WfpDeviceGetContext(WdfIoQueueGetDevice(Queue));
    if (context->IsSensor) {
        WfpDispatchSensorIoctl(Request, IoControlCode);
    } else {
        WfpDispatchControlIoctl(Request, IoControlCode);
    }
}
