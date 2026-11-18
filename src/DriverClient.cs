using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsFingerprintSimulator;

/// <summary>Owns the virtual sensor control-device handle and its vendor IOCTLs.</summary>
internal sealed class DriverClient : IDisposable
{
    private static readonly Guid ControlInterfaceGuid = new("E5D15E8E-9EFC-4762-83E5-17407B10764A");
    private const uint FileDeviceUnknown = 0x22;
    private const uint MethodBuffered = 0;
    private const uint FileWriteAccess = 0x2;

    private static readonly uint IoctlSetTemplate = CtlCode(0x800);
    private static readonly uint IoctlTriggerRead = CtlCode(0x801);

    private IntPtr _handle;
    private bool _disposed;

    public DriverClient()
    {
        if (IoctlSetTemplate != 0x22A000 || IoctlTriggerRead != 0x22A004)
        {
            throw new InvalidOperationException("IOCTL derivation self-check failed; CTL_CODE layout is wrong.");
        }

        string? path = FindControlDevicePath();
        if (path is null)
        {
            throw new InvalidOperationException("Virtual fingerprint control device not found. Install and start the virtual sensor driver.");
        }

        _handle = ControlNative.CreateFileW(
            path,
            ControlNative.GenericRead | ControlNative.GenericWrite,
            ControlNative.FileShareAll,
            IntPtr.Zero,
            ControlNative.OpenExisting,
            0,
            IntPtr.Zero);
        if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"CreateFile failed for the virtual fingerprint device: {new Win32Exception(err).Message}");
        }
    }

    /// <summary>Stages the template then completes one pending sensor capture/probe request.</summary>
    public void SendRead(byte[] template)
    {
        StageTemplate(template);
        if (!TriggerRead())
        {
            int err = Marshal.GetLastWin32Error();
            string detail = err == 0 ? "the device rejected the request" : new Win32Exception(err).Message;
            throw new InvalidOperationException($"TRIGGER_READ failed: {detail}.");
        }
    }

    public void StageTemplate(byte[] template)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(template);

        GCHandle pin = GCHandle.Alloc(template, GCHandleType.Pinned);
        try
        {
            if (!ControlNative.DeviceIoControl(_handle, IoctlSetTemplate, pin.AddrOfPinnedObject(), (uint)template.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"SET_TEMPLATE failed: {new Win32Exception(err).Message}");
            }
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Best-effort trigger; a trigger with no pending request is harmless.</summary>
    public bool TriggerRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ControlNative.DeviceIoControl(_handle, IoctlTriggerRead, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_handle != IntPtr.Zero && _handle != new IntPtr(-1))
        {
            ControlNative.CloseHandle(_handle);
        }

        _handle = IntPtr.Zero;
        _disposed = true;
    }

    private static uint CtlCode(uint function) =>
        (FileDeviceUnknown << 16) | (FileWriteAccess << 14) | (function << 2) | MethodBuffered;

    private static string? FindControlDevicePath()
    {
        Guid guid = ControlInterfaceGuid;
        IntPtr set = ControlNative.SetupDiGetClassDevs(
            ref guid,
            IntPtr.Zero,
            IntPtr.Zero,
            ControlNative.DigcfPresent | ControlNative.DigcfDeviceinterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"SetupDiGetClassDevs failed: {new Win32Exception(err).Message}");
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var ifd = new ControlNative.SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<ControlNative.SpDeviceInterfaceData>(),
                };
                if (!ControlNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref ifd))
                {
                    break; // ERROR_NO_MORE_ITEMS ends enumeration.
                }

                uint required = 0;
                ControlNative.SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required == 0)
                {
                    continue;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize: 4 bytes Size + 2-byte WCHAR on x86; padded to 8 on x64.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (ControlNative.SetupDiGetDeviceInterfaceDetail(set, ref ifd, buffer, required, out _, IntPtr.Zero))
                    {
                        return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    }
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
            ControlNative.SetupDiDestroyDeviceInfoList(set);
        }
    }
}

internal static class ControlNative
{
    internal const uint DigcfPresent = 0x2;
    internal const uint DigcfDeviceinterface = 0x10;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareAll = 0x1 | 0x2 | 0x4;
    internal const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        internal uint CbSize;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
