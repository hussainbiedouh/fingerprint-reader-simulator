using System;
using System.Runtime.InteropServices;

namespace EnrollTool;

/// <summary>P/Invoke surface verified against Winbio.h and Microsoft Learn.</summary>
internal static class WinBioApi
{
    private const string Dll = "winbio.dll";

    internal const uint FactorFingerprint = 0x00000008;
    internal const uint PoolSystem = 1;
    internal const uint SessionFlagDefault = 0;
    internal const byte SubtypeRightIndexFinger = 0x02; // WINBIO_ANSI_381_POS_RH_INDEX_FINGER

    // winbio_err.h: informational, not a failure. 0x80098002 is WINBIO_E_INVALID_UNIT.
    internal const int IMoreData = unchecked((int)0x00090001);
    internal const int BadCapture = unchecked((int)0x80098008);

    internal const int MaxStringLen = 256;
    internal const int GuidSize = 16;
    internal const int MaxSidSize = 68;
    internal const int MaxSecureIdSize = 32;

    internal const int IdentityValueSize = 4 + MaxSidSize;
    internal const int IdentitySize = 4 + IdentityValueSize;
    internal const int UnitSchemaSize = 5 * 4 + 5 * (2 * MaxStringLen) + 2 * 4;

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
    internal static extern int WinBioEnumBiometricUnits(
        uint factor,
        out IntPtr unitSchemaArray,
        out nuint unitCount);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioEnrollBegin(uint sessionHandle, byte subFactor, uint unitId);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioEnrollCapture(uint sessionHandle, out uint rejectDetail);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioEnrollCommit(uint sessionHandle, out Identity identity, out byte isNewTemplate);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioEnrollDiscard(uint sessionHandle);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioAcquireFocus();

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioReleaseFocus();

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioCloseSession(uint sessionHandle);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioFree(IntPtr address);

    internal static bool Failed(int hr) => hr < 0;

    internal static string Hr(int hr) => $"0x{unchecked((uint)hr):X8}";

    /// <summary>Fails fast if CLR marshalling disagrees with the SDK struct sizes.</summary>
    internal static void VerifyLayout()
    {
        Check("WINBIO_IDENTITY", Marshal.SizeOf<Identity>(), IdentitySize);
        Check("WINBIO_UNIT_SCHEMA", Marshal.SizeOf<UnitSchema>(), UnitSchemaSize);
    }

    private static void Check(string name, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"{name} marshals to {actual} bytes; expected {expected}.");
        }
    }
}
