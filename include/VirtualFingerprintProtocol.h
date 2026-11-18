/*++
Virtual fingerprint simulator - shared driver/engine/probe protocol.

This header defines the private GUIDs, vendor IOCTLs and template wire layout
used by the UMDF 2 virtual WBDI sensor, the engine adapter and the control
probe. WinBio class IOCTLs and structures are NOT defined here; they come from
the WDK header winbio_ioctl.h.
--*/

#ifndef VIRTUALFINGERPRINT_PROTOCOL_H
#define VIRTUALFINGERPRINT_PROTOCOL_H

#include <guiddef.h>
#include <devioctl.h>

#ifndef EXTERN_C
#ifdef __cplusplus
#define EXTERN_C extern "C"
#else
#define EXTERN_C extern
#endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

//
// {E5D15E8E-9EFC-4762-83E5-17407B10764A}
// Control device interface. Not a Biometric interface; no WinBio registry.
// One translation unit must include this header with INITGUID defined.
//
#ifdef INITGUID
EXTERN_C const GUID GUID_DEVINTERFACE_WFP_CONTROL = {
    0xe5d15e8e, 0x9efc, 0x4762, {0x83, 0xe5, 0x17, 0x40, 0x7b, 0x10, 0x76, 0x4a}
};
#else
EXTERN_C const GUID GUID_DEVINTERFACE_WFP_CONTROL;
#endif

#define WFP_CTL_CODE(function) \
    CTL_CODE(FILE_DEVICE_UNKNOWN, (function), METHOD_BUFFERED, FILE_WRITE_ACCESS)

// Input:  WFP_TEMPLATE_HEADER followed by Size - sizeof(WFP_TEMPLATE_HEADER)
//         bytes of template (BIR) data. No output.
#define IOCTL_WFP_SET_TEMPLATE   WFP_CTL_CODE(0x800)

// No input. Completes the one pending IOCTL_BIOMETRIC_CAPTURE_DATA request.
#define IOCTL_WFP_TRIGGER_READ   WFP_CTL_CODE(0x801)

#define WFP_TEMPLATE_ID_SIZE     16
#define WFP_TEMPLATE_MAX_SIZE    (64 * 1024)

//
// Fixed prefix of the IOCTL_WFP_SET_TEMPLATE input buffer.
// Size is the TOTAL byte count of the buffer, including this header.
//
typedef struct _WFP_TEMPLATE_HEADER {
    ULONG Size;
    UCHAR Id[WFP_TEMPLATE_ID_SIZE];
} WFP_TEMPLATE_HEADER, *PWFP_TEMPLATE_HEADER;

#ifdef __cplusplus
}
#endif

#endif // VIRTUALFINGERPRINT_PROTOCOL_H
