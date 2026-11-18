using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TriggerTool;

internal static class Program
{
    // Control device interface: {E5D15E8E-9EFC-4762-83E5-17407B10764A}
    private static readonly Guid ControlInterfaceGuid = new("E5D15E8E-9EFC-4762-83E5-17407B10764A");

    // CTL_CODE(FILE_DEVICE_UNKNOWN, fn, METHOD_BUFFERED, FILE_WRITE_ACCESS)
    private const uint FileDeviceUnknown = 0x22;
    private const uint MethodBuffered = 0;
    private const uint FileWriteAccess = 0x2;

    private const int TemplateIdSize = 16;
    private const int TemplateHeaderSize = 4 + TemplateIdSize; // ULONG Size + UCHAR Id[16]
    private const int TemplatePayloadSize = 64;

    private static readonly uint IoctlSetTemplate = CtlCode(0x800);
    private static readonly uint IoctlTriggerRead = CtlCode(0x801);

    private static uint CtlCode(uint function) =>
        (FileDeviceUnknown << 16) | (FileWriteAccess << 14) | (function << 2) | MethodBuffered;

    private static int Main(string[] args)
    {
        if (!PrintIoctlSelfCheck())
        {
            return 1;
        }

        try
        {
            return args switch
            {
                ["set", var name] => SetTemplate(name),
                ["trigger"] => TriggerRead(),
                ["fire", var name] => SetTemplate(name) == 0 ? TriggerRead() : 1,
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TriggerTool failed: {ex.Message}");
            return 1;
        }
    }

    private static bool PrintIoctlSelfCheck()
    {
        Console.WriteLine($"IOCTL_WFP_SET_TEMPLATE = 0x{IoctlSetTemplate:X8}");
        Console.WriteLine($"IOCTL_WFP_TRIGGER_READ = 0x{IoctlTriggerRead:X8}");
        bool matches = IoctlSetTemplate == 0x22A000 && IoctlTriggerRead == 0x22A004;
        Console.WriteLine($"IOCTL self-check: {(matches ? "PASS" : "FAIL")}");
        return matches;
    }

    private static int Usage()
    {
        Console.WriteLine("Usage: TriggerTool set <name-or-id> | trigger | fire <name-or-id>");
        return 1;
    }

    private static string DescribeWin32Error(int err) =>
        $"Win32 error {err} ({new Win32Exception(err).Message})";

    private static int SetTemplate(string name)
    {
        if (!TryOpen(out IntPtr handle))
        {
            return 1;
        }

        // Same SHA-256 digest seeds both the 16-byte id and the 64-byte template payload.
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        byte[] payload = new byte[TemplatePayloadSize];
        digest.CopyTo(payload, 0);
        SHA256.HashData(digest).CopyTo(payload, 32);

        byte[] input = new byte[TemplateHeaderSize + TemplatePayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(input, (uint)input.Length); // Size is total byte count.
        digest.AsSpan(0, TemplateIdSize).CopyTo(input.AsSpan(4));
        payload.CopyTo(input, TemplateHeaderSize);

        Console.WriteLine($"Template '{name}': id={Convert.ToHexString(input, 4, TemplateIdSize)}, total={input.Length}");
        GCHandle pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            return SendIoctl(handle, IoctlSetTemplate, pin.AddrOfPinnedObject(), (uint)input.Length, "SET_TEMPLATE");
        }
        finally
        {
            pin.Free();
            Native.CloseHandle(handle);
        }
    }

    private static int TriggerRead()
    {
        if (!TryOpen(out IntPtr handle))
        {
            return 1;
        }

        try
        {
            return SendIoctl(handle, IoctlTriggerRead, IntPtr.Zero, 0, "TRIGGER_READ");
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static bool TryOpen(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        string? path = FindControlDevicePath();
        if (path is null)
        {
            Console.Error.WriteLine("Control device not found for GUID {E5D15E8E-9EFC-4762-83E5-17407B10764A}.");
            return false;
        }

        Console.WriteLine($"Device path: {path}");
        handle = OpenDevice(path);
        return true;
    }

    private static string? FindControlDevicePath()
    {
        Guid guid = ControlInterfaceGuid;
        IntPtr set = Native.SetupDiGetClassDevs(
            ref guid,
            IntPtr.Zero,
            IntPtr.Zero,
            Native.DigcfPresent | Native.DigcfDeviceinterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"SetupDiGetClassDevs failed: {DescribeWin32Error(err)}");
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var ifd = new Native.SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<Native.SpDeviceInterfaceData>(),
                };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref ifd))
                {
                    break; // ERROR_NO_MORE_ITEMS ends the enumeration.
                }

                uint required = 0;
                Native.SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required == 0)
                {
                    continue;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize: 4 bytes Size + 2 bytes WCHAR on x86; padded to 8 on x64.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref ifd, buffer, required, out _, IntPtr.Zero))
                    {
                        return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    }

                    int err = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"SetupDiGetDeviceInterfaceDetail failed: {DescribeWin32Error(err)}");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return null;
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static IntPtr OpenDevice(string path)
    {
        IntPtr handle = Native.CreateFileW(
            path,
            Native.GenericRead | Native.GenericWrite,
            Native.FileShareAll,
            IntPtr.Zero,
            Native.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"CreateFile failed for {path}: {DescribeWin32Error(err)}");
        }

        return handle;
    }

    private static int SendIoctl(IntPtr handle, uint code, IntPtr input, uint inputSize, string label)
    {
        if (Native.DeviceIoControl(handle, code, input, inputSize, IntPtr.Zero, 0, out uint returned, IntPtr.Zero))
        {
            Console.WriteLine($"{label}: DeviceIoControl succeeded (bytes returned: {returned})");
            return 0;
        }

        int err = Marshal.GetLastWin32Error();
        Console.Error.WriteLine($"{label}: DeviceIoControl failed: {DescribeWin32Error(err)}");
        return 1;
    }
}

internal static class Native
{
    public const uint DigcfPresent = 0x2;
    public const uint DigcfDeviceinterface = 0x10;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareAll = 0x1 | 0x2 | 0x4;
    public const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr detailData,
        uint detailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        IntPtr device,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);
}
