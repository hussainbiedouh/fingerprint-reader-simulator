#pragma once

#include <wdf.h>
#include "..\include\VirtualFingerprintProtocol.h"

typedef struct _WFP_DEVICE_CONTEXT {
    BOOLEAN IsSensor;
    WDFQUEUE Queue;
} WFP_DEVICE_CONTEXT, *PWFP_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(WFP_DEVICE_CONTEXT, WfpDeviceGetContext)

//
// Single driver-wide state. Shared by the sensor and control device objects.
// Every field below is accessed while WfpStateLock is held.
//
extern WDFWAITLOCK WfpStateLock;
extern UCHAR WfpTemplate[WFP_TEMPLATE_MAX_SIZE];
extern ULONG WfpTemplateSize;
extern BOOLEAN WfpTemplateValid;
extern UCHAR WfpCaptureTemplate[WFP_TEMPLATE_MAX_SIZE];
extern ULONG WfpCaptureTemplateSize;
extern BOOLEAN WfpCaptureTemplateValid;
extern WDFREQUEST WfpPendingCapture;

EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL WfpEvtIoDeviceControl;
EVT_WDF_IO_QUEUE_IO_STOP WfpEvtIoStop;
EVT_WDF_REQUEST_CANCEL WfpEvtRequestCancel;

NTSTATUS WfpCompletePendingCapture(NTSTATUS CompletionStatus);
NTSTATUS WfpTriggerPendingCapture(VOID);
