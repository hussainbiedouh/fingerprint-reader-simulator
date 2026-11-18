using System;
using System.Runtime.InteropServices;

namespace WinBioProbe;

/// <summary>P/Invoke surface verified against Winbio.h and Microsoft Learn.</summary>
internal static class WinBioApi
{
    private const string Dll = "winbio.dll";

    internal const uint FactorFingerprint = 0x00000008;
    internal const uint PoolSystem = 1;
    internal const uint SessionFlagDefault = 0;
    internal const uint EventFpUnclaimedIdentify = 0x00000002;

    internal const int MaxStringLen = 256;
    internal const int GuidSize = 16;
    internal const int MaxSidSize = 68;
    internal const int MaxSecureIdSize = 32;

    // AccountSid (4 + 68) is the largest WINBIO_IDENTITY union arm.
    internal const int IdentityValueSize = 4 + MaxSidSize;
    internal const int IdentitySize = 4 + IdentityValueSize;

    // UnitId + Identity + SubFactor + 3 padding bytes + RejectDetail.
    internal const int UnclaimedIdentifySize = 4 + IdentitySize + 1 + 3 + 4;
    internal const int UnitSchemaSize = 5 * 4 + 5 * (2 * MaxStringLen) + 2 * 4;
    internal const int BirDataSize = 4 + 4;
    internal const int BirSize = 4 * BirDataSize;

    internal enum IdentityType : uint
    {
        Null = 0,
        Wildcard = 1,
        Guid = 2,
        Sid = 3,
        SecureId = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Version
    {
        public uint MajorVersion;
        public uint MinorVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BirData
    {
        public uint Size;
        public uint Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Bir
    {
        public BirData HeaderBlock;
        public BirData StandardDataBlock;
        public BirData VendorDataBlock;
        public BirData SignatureBlock;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IdentityValue
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = IdentityValueSize)]
        public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Identity
    {
        public uint Type;
        public IdentityValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventParameters
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = UnclaimedIdentifySize)]
        public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Event
    {
        public uint Type;
        public EventParameters Parameters;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct UnitSchema
    {
        public uint UnitId;
        public uint PoolType;
        public uint BiometricFactor;
        public uint SensorSubType;
        public uint Capabilities;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] public string DeviceInstanceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] public string Model;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] public string SerialNumber;

        public Version FirmwareVersion;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EventCallback(IntPtr context, int operationStatus, IntPtr eventPtr);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioOpenSession(
        uint factor,
        uint poolType,
        uint flags,
        IntPtr unitArray,
        nuint unitCount,
        IntPtr databaseId,
        out uint sessionHandle);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioRegisterEventMonitor(
        uint sessionHandle,
        uint eventMask,
        EventCallback callback,
        IntPtr context);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioUnregisterEventMonitor(uint sessionHandle);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioEnumBiometricUnits(
        uint factor,
        out IntPtr unitSchemaArray,
        out nuint unitCount);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioLocateSensor(uint sessionHandle, out uint unitId);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioIdentify(
        uint sessionHandle,
        out uint unitId,
        out Identity identity,
        out byte subFactor,
        out uint rejectDetail);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioFree(IntPtr address);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioCloseSession(uint sessionHandle);

    internal static bool Failed(int hr) => hr < 0;

    internal static string Hr(int hr) => $"0x{unchecked((uint)hr):X8}";
}

/// <summary>Fails fast if CLR marshalling disagrees with the SDK struct sizes.</summary>
internal static class WinBioLayout
{
    internal static void Verify()
    {
        Check("WINBIO_BIR_DATA", Marshal.SizeOf<WinBioApi.BirData>(), WinBioApi.BirDataSize);
        Check("WINBIO_BIR", Marshal.SizeOf<WinBioApi.Bir>(), WinBioApi.BirSize);
        Check("WINBIO_IDENTITY", Marshal.SizeOf<WinBioApi.Identity>(), WinBioApi.IdentitySize);
        Check("WINBIO_EVENT", Marshal.SizeOf<WinBioApi.Event>(), 4 + WinBioApi.UnclaimedIdentifySize);
        Check("WINBIO_UNIT_SCHEMA", Marshal.SizeOf<WinBioApi.UnitSchema>(), WinBioApi.UnitSchemaSize);
    }

    private static void Check(string name, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"{name} marshals to {actual} bytes; expected {expected}.");
        }
    }
}
