using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EnrollTool;

internal static class Program
{
    private const int TemplateIdSize = 16;
    private const int TemplateHeaderSize = 4 + TemplateIdSize; // ULONG Size + UCHAR Id[16]
    private const int TemplatePayloadSize = 64;

    private const int PendingCaptureDelayMs = 250;
    private const int TriggerRetryDelayMs = 100;
    private const int EnrollmentTimeoutMs = 15000;
    private const int MaxCaptures = 12;

    // Control device interface: {E5D15E8E-9EFC-4762-83E5-17407B10764A}
    private static readonly Guid ControlInterfaceGuid = new("E5D15E8E-9EFC-4762-83E5-17407B10764A");

    private static int Main(string[] args)
    {
        Console.WriteLine($"IOCTL_WFP_SET_TEMPLATE = 0x{Native.IoctlSetTemplate:X8}");
        Console.WriteLine($"IOCTL_WFP_TRIGGER_READ = 0x{Native.IoctlTriggerRead:X8}");
        if (Native.IoctlSetTemplate != 0x22A000 || Native.IoctlTriggerRead != 0x22A004)
        {
            Console.Error.WriteLine("IOCTL self-check failed; CTL_CODE derivation is wrong.");
            return 1;
        }

        try
        {
            WinBioApi.VerifyLayout();

            if (args.Length != 1)
            {
                return Usage();
            }

            return args[0].Equals("list", StringComparison.OrdinalIgnoreCase)
                ? ListUnits()
                : Enroll(args[0]);
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("winbio.dll is not available on this machine.");
            return 1;
        }
        catch (EntryPointNotFoundException ex)
        {
            Console.Error.WriteLine($"WinBio entry point missing: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"EnrollTool failed: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("Usage: EnrollTool <name> | list");
        return 1;
    }

    private static int Enroll(string name)
    {
        byte[] template = BuildTemplate(name);
        Console.WriteLine($"Template '{name}': id={Convert.ToHexString(template, 4, TemplateIdSize)}, total={template.Length}");

        if (!TryOpenControl(out IntPtr control))
        {
            return 1;
        }

        uint session = 0;
        bool focusAcquired = false;
        bool sessionOpen = false;
        bool enrollInProgress = false;
        bool timedOut = false;

        try
        {
            int hr = WinBioApi.WinBioAcquireFocus();
            if (WinBioApi.Failed(hr))
            {
                // WinBioAcquireFocus requires Local System; a console tool falls back to foreground.
                Console.WriteLine($"WinBioAcquireFocus note: {WinBioApi.Hr(hr)} ({HrMessage(hr)}); using console foreground.");
                if (!TryUseConsoleForeground())
                {
                    Console.Error.WriteLine("Cannot take biometric focus: console window is not foreground. Run from an interactive console with focus.");
                    return 1;
                }
            }
            else
            {
                focusAcquired = true;
            }

            Console.WriteLine("Opening WinBio system-pool session...");
            hr = WithTimeout(() => WinBioApi.WinBioOpenSession(
                WinBioApi.FactorFingerprint,
                WinBioApi.PoolSystem,
                WinBioApi.SessionFlagDefault,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                out session), "WinBioOpenSession");
            if (WinBioApi.Failed(hr))
            {
                return Fail("WinBioOpenSession", hr);
            }
            sessionOpen = true;

            // WinBioLocateSensor waits for a physical touch; enumerate units instead.
            if (!TryGetUnitId(out uint unitId, out int enumHr))
            {
                return enumHr != 0 ? Fail("WinBioEnumBiometricUnits", enumHr) : 1;
            }

            hr = WithTimeout(
                () => WinBioApi.WinBioEnrollBegin(session, WinBioApi.SubtypeRightIndexFinger, unitId),
                "WinBioEnrollBegin");
            if (WinBioApi.Failed(hr))
            {
                return Fail("WinBioEnrollBegin", hr);
            }
            enrollInProgress = true;

            bool templateComplete = false;
            for (int capture = 1; capture <= MaxCaptures; capture++)
            {
                Console.WriteLine($"Capture {capture}: staging template id={Convert.ToHexString(template, 4, TemplateIdSize)}");
                int captureHr = CaptureOnce(session, control, template, out uint rejectDetail);
                Console.WriteLine($"Capture {capture} result: {WinBioApi.Hr(captureHr)}, reject={rejectDetail}");

                if (captureHr == WinBioApi.IMoreData || captureHr == WinBioApi.BadCapture)
                {
                    continue;
                }
                if (WinBioApi.Failed(captureHr))
                {
                    return Fail("WinBioEnrollCapture", captureHr);
                }

                templateComplete = true;
                break;
            }

            if (!templateComplete)
            {
                Console.Error.WriteLine($"Enrollment did not complete after {MaxCaptures} captures.");
                return 1;
            }

            hr = WinBioApi.WinBioEnrollCommit(session, out WinBioApi.Identity identity, out byte isNewTemplate);
            if (WinBioApi.Failed(hr))
            {
                return Fail("WinBioEnrollCommit", hr);
            }
            enrollInProgress = false;

            Console.WriteLine($"Committed identity (isNewTemplate={isNewTemplate != 0}):");
            PrintIdentity(identity);
            return 0;
        }
        catch (TimeoutException ex)
        {
            timedOut = true;
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Native.CloseHandle(control);
            if (!timedOut)
            {
                if (enrollInProgress && sessionOpen)
                {
                    WinBioApi.WinBioEnrollDiscard(session);
                }
                if (sessionOpen)
                {
                    WinBioApi.WinBioCloseSession(session);
                }
                if (focusAcquired)
                {
                    WinBioApi.WinBioReleaseFocus();
                }
            }
        }
    }

    /// <summary>Runs one blocking WinBio capture while feeding the virtual sensor from this thread.</summary>
    private static int CaptureOnce(uint session, IntPtr control, byte[] template, out uint rejectDetail)
    {
        uint reject = 0;
        rejectDetail = 0;

        // The driver freezes the staged template when the capture request arrives,
        // so stage before WinBioEnrollCapture can issue that request.
        StageTemplate(control, template);
        Task<int> capture = Task.Run(() => WinBioApi.WinBioEnrollCapture(session, out reject));

        // shortcut: fixed delay lets the pending WBDI capture reach the driver; no ready-signal exists.
        Thread.Sleep(PendingCaptureDelayMs);

        DateTime deadline = DateTime.UtcNow.AddMilliseconds(EnrollmentTimeoutMs);
        while (!capture.IsCompleted && DateTime.UtcNow < deadline)
        {
            // A trigger with no pending request is harmless; retry until the capture completes.
            TryTriggerRead(control);
            if (!capture.IsCompleted)
            {
                Thread.Sleep(TriggerRetryDelayMs);
            }
        }

        if (!capture.IsCompleted)
        {
            throw new TimeoutException("WinBioEnrollCapture timed out waiting for the virtual sensor.");
        }

        rejectDetail = reject;
        return capture.GetAwaiter().GetResult();
    }

    private static int WithTimeout(Func<int> call, string operation)
    {
        Task<int> task = Task.Run(call);
        Task winner = Task.WhenAny(task, Task.Delay(EnrollmentTimeoutMs)).GetAwaiter().GetResult();
        if (winner != task)
        {
            throw new TimeoutException($"{operation} timed out after {EnrollmentTimeoutMs} ms.");
        }
        return task.GetAwaiter().GetResult();
    }

    private static byte[] BuildTemplate(string name)
    {
        // Same SHA-256 derivation as TriggerTool: deterministic id and payload.
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        byte[] payload = new byte[TemplatePayloadSize];
        digest.CopyTo(payload, 0);
        SHA256.HashData(digest).CopyTo(payload, 32);

        byte[] input = new byte[TemplateHeaderSize + TemplatePayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(input, (uint)input.Length);
        digest.AsSpan(0, TemplateIdSize).CopyTo(input.AsSpan(4));
        payload.CopyTo(input, TemplateHeaderSize);
        return input;
    }

    private static void StageTemplate(IntPtr control, byte[] template)
    {
        GCHandle pin = GCHandle.Alloc(template, GCHandleType.Pinned);
        try
        {
            if (!Native.DeviceIoControl(
                    control,
                    Native.IoctlSetTemplate,
                    pin.AddrOfPinnedObject(),
                    (uint)template.Length,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"SET_TEMPLATE failed: {DescribeWin32Error(err)}");
            }
        }
        finally
        {
            pin.Free();
        }
    }

    private static bool TryTriggerRead(IntPtr control) =>
        Native.DeviceIoControl(control, Native.IoctlTriggerRead, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    private static bool TryOpenControl(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        string? found = FindControlDevicePath();
        if (found is null)
        {
            Console.Error.WriteLine($"Control device not found for GUID {ControlInterfaceGuid:B}.");
            return false;
        }

        Console.WriteLine($"Device path: {found}");
        handle = Native.CreateFileW(
            found,
            Native.GenericRead | Native.GenericWrite,
            Native.FileShareAll,
            IntPtr.Zero,
            Native.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"CreateFile failed for {found}: {DescribeWin32Error(err)}");
        }
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
                    Console.Error.WriteLine($"SetupDiGetDeviceInterfaceDetail failed: {DescribeWin32Error(Marshal.GetLastWin32Error())}");
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

    private static bool TryUseConsoleForeground()
    {
        IntPtr hwnd = Native.GetConsoleWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr foreground = Native.GetForegroundWindow();
        uint foregroundThread = Native.GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint currentThread = Native.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
                        Native.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            Native.ShowWindow(hwnd, 9); // SW_RESTORE
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                Native.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
        IntPtr nowForeground = Native.GetForegroundWindow();
        Console.WriteLine($"Focus: console=0x{hwnd.ToInt64():X}, foreground=0x{nowForeground.ToInt64():X}");
        return nowForeground == hwnd;
    }

    private static bool TryGetUnitId(out uint unitId, out int hr)
    {
        unitId = 0;
        hr = 0;
        IntPtr array = IntPtr.Zero;
        try
        {
            hr = WinBioApi.WinBioEnumBiometricUnits(WinBioApi.FactorFingerprint, out array, out nuint count);
            if (WinBioApi.Failed(hr))
            {
                return false;
            }
            if (count == 0 || array == IntPtr.Zero)
            {
                Console.Error.WriteLine("No fingerprint biometric unit found.");
                return false;
            }

            unitId = Marshal.PtrToStructure<WinBioApi.UnitSchema>(array).UnitId;
            if (unitId == 0)
            {
                Console.Error.WriteLine("Fingerprint biometric unit id is zero.");
                return false;
            }

            Console.WriteLine($"Sensor unit: {unitId}");
            return true;
        }
        finally
        {
            if (array != IntPtr.Zero)
            {
                WinBioApi.WinBioFree(array);
            }
        }
    }

    private static int ListUnits()
    {
        IntPtr array = IntPtr.Zero;
        try
        {
            int hr = WinBioApi.WinBioEnumBiometricUnits(WinBioApi.FactorFingerprint, out array, out nuint count);
            if (WinBioApi.Failed(hr))
            {
                Console.Error.WriteLine($"WinBioEnumBiometricUnits failed: {WinBioApi.Hr(hr)} ({HrMessage(hr)})");
                return 1;
            }

            Console.WriteLine($"Fingerprint units: {count}");
            int stride = Marshal.SizeOf<WinBioApi.UnitSchema>();
            for (nuint i = 0; i < count; i++)
            {
                int offset = checked((int)(i * (nuint)stride));
                WinBioApi.UnitSchema unit = Marshal.PtrToStructure<WinBioApi.UnitSchema>(IntPtr.Add(array, offset));
                string description = string.IsNullOrWhiteSpace(unit.Description) ? "(no description)" : unit.Description;
                Console.WriteLine($"  Unit {unit.UnitId}: {description}");
            }
            return 0;
        }
        finally
        {
            if (array != IntPtr.Zero)
            {
                WinBioApi.WinBioFree(array);
            }
        }
    }

    private static void PrintIdentity(in WinBioApi.Identity identity)
    {
        byte[] bytes = identity.Value.Bytes ?? Array.Empty<byte>();
        switch ((WinBioApi.IdentityType)identity.Type)
        {
            case WinBioApi.IdentityType.Guid:
                Console.WriteLine(bytes.Length >= WinBioApi.GuidSize
                    ? $"  Identity: GUID {new Guid(bytes.AsSpan(0, WinBioApi.GuidSize))}"
                    : "  Identity: GUID (truncated)");
                break;
            case WinBioApi.IdentityType.Sid:
                uint size = bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0;
                if (size == 0 || size > (uint)(bytes.Length - 4))
                {
                    Console.WriteLine($"  Identity: SID (invalid size {size})");
                    break;
                }
                byte[] sidBytes = bytes.AsSpan(4, (int)size).ToArray();
                try
                {
                    Console.WriteLine($"  Identity: SID {new SecurityIdentifier(sidBytes, 0).Value}");
                }
                catch (ArgumentException)
                {
                    Console.WriteLine($"  Identity: SID (raw) {Convert.ToHexString(sidBytes)}");
                }
                break;
            case WinBioApi.IdentityType.SecureId:
                Console.WriteLine(bytes.Length >= WinBioApi.MaxSecureIdSize
                    ? $"  Identity: SecureId {Convert.ToHexString(bytes.AsSpan(0, WinBioApi.MaxSecureIdSize))}"
                    : "  Identity: SecureId (truncated)");
                break;
            case WinBioApi.IdentityType.Wildcard:
                Console.WriteLine("  Identity: wildcard");
                break;
            default:
                Console.WriteLine("  Identity: none");
                break;
        }
    }

    private static int Fail(string operation, int hr)
    {
        Console.Error.WriteLine($"{operation} failed: {WinBioApi.Hr(hr)} ({HrMessage(hr)})");
        return 1;
    }

    private static string HrMessage(int hr) => Marshal.GetExceptionForHR(hr)?.Message ?? "unknown error";

    private static string DescribeWin32Error(int err) => new Win32Exception(err).Message;
}
