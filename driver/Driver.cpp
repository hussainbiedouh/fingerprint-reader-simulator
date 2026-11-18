#include <windows.h>
#include <wdf.h>
#define INITGUID
#include <devpkey.h>
#include <winbio_ioctl.h>
#include "Driver.h"

// guiddef.h was already included by windows.h, so DEFINE_GUID in
// winbio_ioctl.h only declares this GUID. Define it once here.
EXTERN_C const GUID GUID_DEVINTERFACE_BIOMETRIC_READER = {
    0xe2b5183a, 0x99ea, 0x4cc3, {0xad, 0x6b, 0x80, 0xca, 0x8d, 0x71, 0x5b, 0x80}
};

WDFWAITLOCK WfpStateLock;
UCHAR WfpTemplate[WFP_TEMPLATE_MAX_SIZE];
ULONG WfpTemplateSize;
BOOLEAN WfpTemplateValid;
UCHAR WfpCaptureTemplate[WFP_TEMPLATE_MAX_SIZE];
ULONG WfpCaptureTemplateSize;
BOOLEAN WfpCaptureTemplateValid;
WDFREQUEST WfpPendingCapture;

EVT_WDF_DRIVER_DEVICE_ADD WfpEvtDeviceAdd;

extern "C" NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDFDRIVER driver;
    NTSTATUS status;

    WDF_DRIVER_CONFIG_INIT(&config, WfpEvtDeviceAdd);
    status = WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, &driver);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = driver;
    return WdfWaitLockCreate(&attributes, &WfpStateLock);
}

static ULONG
WfpStringLength(
    _In_ PCWSTR Value
    )
{
    ULONG length = 0;
    while (Value[length] != L'\0') {
        length++;
    }
    return length;
}

static BOOLEAN
WfpHardwareIdMatches(
    _In_ PWCHAR HardwareIds,
    _In_ ULONG BufferBytes,
    _In_ PCWSTR Wanted
    )
{
    PWCHAR end = HardwareIds + BufferBytes / sizeof(WCHAR);

    for (PWCHAR id = HardwareIds; id < end && *id != L'\0'; ) {
        ULONG length;
        if (_wcsicmp(id, Wanted) == 0) {
            return TRUE;
        }
        length = WfpStringLength(id);
        if (id + length + 1 >= end) {
            break;
        }
        id += length + 1;
    }
    return FALSE;
}

static NTSTATUS
WfpIsSensorDevice(
    _In_ WDFDEVICE Device,
    _Out_ PBOOLEAN IsSensor
    )
{
    WDF_DEVICE_PROPERTY_DATA property;
    DEVPROPTYPE propertyType;
    WCHAR hardwareIds[256];
    ULONG required = 0;
    NTSTATUS status;

    RtlZeroMemory(hardwareIds, sizeof(hardwareIds));
    WDF_DEVICE_PROPERTY_DATA_INIT(&property, &DEVPKEY_Device_HardwareIds);
    status = WdfDeviceQueryPropertyEx(Device, &property, sizeof(hardwareIds), hardwareIds,
                                      &required, &propertyType);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    if (propertyType != DEVPROP_TYPE_STRING_LIST || required > sizeof(hardwareIds)) {
        return STATUS_INVALID_PARAMETER;
    }

    if (WfpHardwareIdMatches(hardwareIds, sizeof(hardwareIds), L"ROOT\\WFP_SENSOR")) {
        *IsSensor = TRUE;
    } else if (WfpHardwareIdMatches(hardwareIds, sizeof(hardwareIds), L"ROOT\\WFP_CONTROL")) {
        *IsSensor = FALSE;
    } else {
        return STATUS_OBJECT_NAME_NOT_FOUND;
    }
    return STATUS_SUCCESS;
}

NTSTATUS
NTAPI
WfpEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_OBJECT_ATTRIBUTES attributes;
    WFP_DEVICE_CONTEXT *context;
    const GUID *interfaceGuid;
    WDFDEVICE device;
    BOOLEAN isSensor;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Driver);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, WFP_DEVICE_CONTEXT);

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = WfpIsSensorDevice(device, &isSensor);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    context = WfpDeviceGetContext(device);
    context->IsSensor = isSensor;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = WfpEvtIoDeviceControl;
    queueConfig.EvtIoStop = WfpEvtIoStop;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &context->Queue);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    interfaceGuid = isSensor ? &GUID_DEVINTERFACE_BIOMETRIC_READER
                             : &GUID_DEVINTERFACE_WFP_CONTROL;
    return WdfDeviceCreateDeviceInterface(device, interfaceGuid, NULL);
}
