using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;

namespace WindowsFingerprintSimulator;

/// <summary>Enrolls a template into WBS on a background thread using the proven EnrollTool sequence.</summary>
internal static class WinBioEnroller
{
    internal const int TemplateIdOffset = 4;
    internal const int TemplateIdSize = 16;
    private const int TemplateHeaderSize = 4 + TemplateIdSize;
    private const int TemplatePayloadSize = 64;

    private const int PendingCaptureDelayMs = 250;
    private const int TriggerRetryDelayMs = 100;
    private const int EnrollmentTimeoutMs = 15000;
    private const int MaxCaptures = 3;

    public static byte[] BuildTemplate(string name)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        byte[] payload = new byte[TemplatePayloadSize];
        digest.CopyTo(payload, 0);
        SHA256.HashData(digest).CopyTo(payload, 32);

        byte[] input = new byte[TemplateHeaderSize + TemplatePayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(input, (uint)input.Length);
        digest.AsSpan(0, TemplateIdSize).CopyTo(input.AsSpan(TemplateIdOffset));
        payload.CopyTo(input, TemplateHeaderSize);
        return input;
    }

    public static Task<string> EnrollAsync(byte[] template, DriverClient driver, IntPtr hwnd, Action<string>? progress) =>
        Task.Run(() => Enroll(template, driver, hwnd, progress));

    private static string Enroll(byte[] template, DriverClient driver, IntPtr hwnd, Action<string>? progress)
    {
        WinBioNative.VerifyLayout();

        uint session = 0;
        bool focusAcquired = false;
        bool sessionOpen = false;
        bool enrollInProgress = false;
        bool timedOut = false;

        try
        {
            // WinBio focus needs Local System; a GUI must own the foreground window instead.
            bool foregroundTaken = TakeForeground(hwnd, progress);
            int hr = WinBioNative.WinBioAcquireFocus();
            if (WinBioNative.Failed(hr))
            {
                progress?.Invoke($"WinBioAcquireFocus: {WinBioNative.Hr(hr)}, foreground taken={foregroundTaken}");
                if (!foregroundTaken)
                {
                    throw new InvalidOperationException("Cannot acquire biometric focus: the simulator window could not be brought to the foreground.");
                }
            }
            else
            {
                focusAcquired = true;
                progress?.Invoke($"WinBioAcquireFocus: {WinBioNative.Hr(hr)}, acquired=true");
            }

            hr = WithTimeout(() => WinBioNative.WinBioOpenSession(
                WinBioNative.FactorFingerprint,
                WinBioNative.PoolSystem,
                WinBioNative.SessionFlagDefault,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                out session), "WinBioOpenSession");
            if (WinBioNative.Failed(hr))
            {
                throw HrError("WinBioOpenSession", hr);
            }
            sessionOpen = true;

            if (!TryGetUnitId(out uint unitId, out int enumHr))
            {
                throw enumHr != 0
                    ? HrError("WinBioEnumBiometricUnits", enumHr)
                    : new InvalidOperationException("No fingerprint biometric unit was found.");
            }

            hr = WithTimeout(() => WinBioNative.WinBioEnrollBegin(session, WinBioNative.SubtypeRightIndexFinger, unitId), "WinBioEnrollBegin");
            if (WinBioNative.Failed(hr))
            {
                throw HrError("WinBioEnrollBegin", hr);
            }
            enrollInProgress = true;

            bool complete = false;
            for (int capture = 1; capture <= MaxCaptures; capture++)
            {
                driver.StageTemplate(template);
                uint rejectDetail = 0;
                Task<int> captureTask = Task.Run(() => WinBioNative.WinBioEnrollCapture(session, out rejectDetail));

                // shortcut: fixed delay lets the pending WBDI capture reach the driver; no ready-signal exists.
                Thread.Sleep(PendingCaptureDelayMs);
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(EnrollmentTimeoutMs);
                while (!captureTask.IsCompleted && DateTime.UtcNow < deadline)
                {
                    driver.TriggerRead();
                    if (!captureTask.IsCompleted)
                    {
                        Thread.Sleep(TriggerRetryDelayMs);
                    }
                }

                if (!captureTask.IsCompleted)
                {
                    throw new TimeoutException("WinBioEnrollCapture timed out waiting for the virtual sensor.");
                }

                int captureHr = captureTask.GetAwaiter().GetResult();
                progress?.Invoke($"Capture {capture}: {WinBioNative.Hr(captureHr)} reject={rejectDetail}");
                if (captureHr == WinBioNative.IMoreData || captureHr == WinBioNative.BadCapture)
                {
                    continue;
                }
                if (WinBioNative.Failed(captureHr))
                {
                    throw HrError("WinBioEnrollCapture", captureHr, rejectDetail);
                }

                complete = true;
                break;
            }

            if (!complete)
            {
                throw new InvalidOperationException($"Enrollment did not complete after {MaxCaptures} captures.");
            }

            hr = WinBioNative.WinBioEnrollCommit(session, out WinBioNative.Identity identity, out byte isNewTemplate);
            if (WinBioNative.Failed(hr))
            {
                throw HrError("WinBioEnrollCommit", hr);
            }
            enrollInProgress = false;

            return $"{FormatIdentity(identity)}{(isNewTemplate != 0 ? " (new template)" : string.Empty)}";
        }
        catch (TimeoutException)
        {
            // A timed-out capture P/Invoke may still be in flight; WinBio cleanup would race it.
            timedOut = true;
            throw;
        }
        finally
        {
            if (!timedOut)
            {
                if (enrollInProgress && sessionOpen)
                {
                    WinBioNative.WinBioEnrollDiscard(session);
                }
                if (sessionOpen)
                {
                    WinBioNative.WinBioCloseSession(session);
                }
                if (focusAcquired)
                {
                    WinBioNative.WinBioReleaseFocus();
                }
            }
        }
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

    private static bool TryGetUnitId(out uint unitId, out int hr)
    {
        unitId = 0;
        hr = 0;
        IntPtr array = IntPtr.Zero;
        try
        {
            hr = WinBioNative.WinBioEnumBiometricUnits(WinBioNative.FactorFingerprint, out array, out nuint count);
            if (WinBioNative.Failed(hr) || count == 0 || array == IntPtr.Zero)
            {
                return false;
            }

            unitId = Marshal.PtrToStructure<WinBioNative.UnitSchema>(array).UnitId;
            return unitId != 0;
        }
        finally
        {
            if (array != IntPtr.Zero)
            {
                WinBioNative.WinBioFree(array);
            }
        }
    }

    /// <summary>Forces the WinForms window to foreground using the proven EnrollTool sequence.</summary>
    private static bool TakeForeground(IntPtr hwnd, Action<string>? progress)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr foreground = WinBioNative.GetForegroundWindow();
        uint foregroundThread = WinBioNative.GetWindowThreadProcessId(foreground, IntPtr.Zero);
        uint currentThread = WinBioNative.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
                        WinBioNative.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            WinBioNative.ShowWindow(hwnd, 9); // SW_RESTORE
            WinBioNative.BringWindowToTop(hwnd);
            WinBioNative.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                WinBioNative.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        IntPtr nowForeground = WinBioNative.GetForegroundWindow();
        bool taken = nowForeground == hwnd;
        progress?.Invoke($"Foreground: target=0x{hwnd.ToInt64():X}, actual=0x{nowForeground.ToInt64():X}, taken={taken}");
        return taken;
    }

    private static string FormatIdentity(in WinBioNative.Identity identity)
    {
        byte[] bytes = identity.Value.Bytes ?? Array.Empty<byte>();
        switch ((WinBioNative.IdentityType)identity.Type)
        {
            case WinBioNative.IdentityType.Guid:
                return bytes.Length >= WinBioNative.GuidSize
                    ? $"GUID {new Guid(bytes.AsSpan(0, WinBioNative.GuidSize))}"
                    : "GUID (truncated)";
            case WinBioNative.IdentityType.Sid:
                uint size = bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0;
                if (size == 0 || size > (uint)(bytes.Length - 4))
                {
                    return $"SID (invalid size {size})";
                }
                try
                {
                    return "SID " + new SecurityIdentifier(bytes, 4).Value;
                }
                catch (ArgumentException)
                {
                    return "SID (raw) " + Convert.ToHexString(bytes.AsSpan(4, (int)size));
                }
            case WinBioNative.IdentityType.SecureId:
                return bytes.Length >= WinBioNative.MaxSecureIdSize
                    ? "SecureId " + Convert.ToHexString(bytes.AsSpan(0, WinBioNative.MaxSecureIdSize))
                    : "SecureId (truncated)";
            case WinBioNative.IdentityType.Wildcard:
                return "wildcard";
            default:
                return "none";
        }
    }

    private static Exception HrError(string operation, int hr, uint rejectDetail = 0)
    {
        string message = Marshal.GetExceptionForHR(hr)?.Message ?? "unknown error";
        string reject = rejectDetail == 0 ? string.Empty : $", reject detail {rejectDetail}";
        return new InvalidOperationException($"{operation} failed: 0x{unchecked((uint)hr):X8} ({message}){reject}");
    }
}

internal static class WinBioNative
{
    private const string Dll = "winbio.dll";

    internal const uint FactorFingerprint = 0x00000008;
    internal const uint PoolSystem = 1;
    internal const uint SessionFlagDefault = 0;
    internal const byte SubtypeRightIndexFinger = 0x02;

    internal const int IMoreData = unchecked((int)0x00090001);
    internal const int BadCapture = unchecked((int)0x80098008);

    internal const int MaxStringLen = 256;
    internal const int GuidSize = 16;
    internal const int MaxSecureIdSize = 32;
    internal const int IdentityValueSize = 4 + 68;
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
        internal uint MajorVersion;
        internal uint MinorVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IdentityValue
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = IdentityValueSize)]
        internal byte[] Bytes = Array.Empty<byte>();

        public IdentityValue() { }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Identity
    {
        internal uint Type;
        internal IdentityValue Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct UnitSchema
    {
        internal uint UnitId;
        internal uint PoolType;
        internal uint BiometricFactor;
        internal uint SensorSubType;
        internal uint Capabilities;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] internal string DeviceInstanceId = string.Empty;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] internal string Description = string.Empty;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] internal string Manufacturer = string.Empty;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] internal string Model = string.Empty;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStringLen)] internal string SerialNumber = string.Empty;

        internal Version FirmwareVersion;

        public UnitSchema() { }
    }

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioOpenSession(uint factor, uint poolType, uint flags, IntPtr unitArray, nuint unitCount, IntPtr databaseId, out uint sessionHandle);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern int WinBioEnumBiometricUnits(uint factor, out IntPtr unitSchemaArray, out nuint unitCount);

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

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    internal static bool Failed(int hr) => hr < 0;

    internal static string Hr(int hr) => $"0x{unchecked((uint)hr):X8}";

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
