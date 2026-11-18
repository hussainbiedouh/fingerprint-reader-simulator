using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace WinBioProbe;

internal static class Program
{
    // Keep the delegate alive for the whole process; native code holds a raw pointer.
    private static readonly WinBioApi.EventCallback EventCallback = OnBioEvent;
    private static readonly ManualResetEvent EventReady = new(false);
    private static volatile bool eventWasIdentify;

    private const int EventUnitIdOffset = 0;
    private const int EventIdentityOffset = EventUnitIdOffset + 4;
    private const int EventSubFactorOffset = EventIdentityOffset + WinBioApi.IdentitySize;
    private const int EventRejectDetailOffset = EventSubFactorOffset + 4; // subtype + 3 padding bytes
    private const int EventRequiredSize = EventRejectDetailOffset + 4;

    private static int Main(string[] args)
    {
        try
        {
            WinBioLayout.Verify();

            if (args.Length != 1)
            {
                return Usage();
            }

            return args[0].ToLowerInvariant() switch
            {
                "list" => ListUnits(),
                "monitor" => Monitor(),
                "identify" => Identify(),
                _ => Usage(),
            };
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
            Console.Error.WriteLine($"Probe failed: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("Usage: probe list | monitor | identify");
        return 0;
    }

    private static int ListUnits()
    {
        IntPtr array = IntPtr.Zero;
        nuint count = 0;

        try
        {
            int hr = WinBioApi.WinBioEnumBiometricUnits(WinBioApi.FactorFingerprint, out array, out count);
            if (WinBioApi.Failed(hr))
            {
                Console.WriteLine($"No fingerprint units available or enumeration failed ({WinBioApi.Hr(hr)}).");
                return 0;
            }

            Console.WriteLine($"Fingerprint units: {count}");
            if (count == 0 || array == IntPtr.Zero)
            {
                Console.WriteLine("No fingerprint device found.");
                return 0;
            }

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

    private static int Identify()
    {
        if (!TryOpenSession(out uint session))
        {
            return 0;
        }

        try
        {
            IdentifyOnSession(session);
            return 0;
        }
        finally
        {
            WinBioApi.WinBioCloseSession(session);
        }
    }

    private static int Monitor()
    {
        eventWasIdentify = false;
        EventReady.Reset();

        uint monitorSession = 0;
        uint operationSession = 0;
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            // Session A: event monitor only. WinBio docs forbid other API calls on a monitor session.
            if (!TryOpenSession(out monitorSession))
            {
                return 0;
            }

            int hr = WinBioApi.WinBioRegisterEventMonitor(
                monitorSession,
                WinBioApi.EventFpUnclaimedIdentify,
                EventCallback,
                IntPtr.Zero);
            if (WinBioApi.Failed(hr))
            {
                Console.WriteLine($"Could not register event monitor ({WinBioApi.Hr(hr)}).");
                return 0;
            }

            // Session B: separate session for WinBioLocateSensor / WinBioIdentify.
            if (!TryOpenSession(out operationSession))
            {
                return 0;
            }

            Console.WriteLine("Waiting for WINBIO_EVENT_FP_UNCLAIMED_IDENTIFY (Ctrl+C to stop)...");
            EventReady.WaitOne();

            if (!eventWasIdentify)
            {
                Console.WriteLine("No identify event received; stopping.");
                return 0;
            }

            Console.WriteLine("Identify event received; running WinBioIdentify on the operation session.");
            IdentifyOnSession(operationSession);
            return 0;
        }
        finally
        {
            if (operationSession != 0)
            {
                WinBioApi.WinBioCloseSession(operationSession);
            }

            if (monitorSession != 0)
            {
                WinBioApi.WinBioUnregisterEventMonitor(monitorSession);
                WinBioApi.WinBioCloseSession(monitorSession);
            }

            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    private static bool TryOpenSession(out uint session)
    {
        int hr = WinBioApi.WinBioOpenSession(
            WinBioApi.FactorFingerprint,
            WinBioApi.PoolSystem,
            WinBioApi.SessionFlagDefault,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            out session);

        if (WinBioApi.Failed(hr))
        {
            Console.WriteLine($"No fingerprint device available or session open failed ({WinBioApi.Hr(hr)}).");
            return false;
        }

        return true;
    }

    private static void IdentifyOnSession(uint session)
    {
        int hr = WinBioApi.WinBioLocateSensor(session, out uint locatedUnit);
        if (WinBioApi.Failed(hr))
        {
            Console.WriteLine($"WinBioLocateSensor failed ({WinBioApi.Hr(hr)}).");
        }
        else
        {
            Console.WriteLine($"Sensor unit: {locatedUnit}");
        }

        Console.WriteLine("Touch the sensor now...");
        hr = WinBioApi.WinBioIdentify(session, out uint unit, out WinBioApi.Identity identity, out byte subFactor, out uint rejectDetail);
        if (WinBioApi.Failed(hr))
        {
            Console.WriteLine($"WinBioIdentify failed ({WinBioApi.Hr(hr)}).");
            return;
        }

        Console.WriteLine($"WinBioIdentify: unit={unit}, subtype=0x{subFactor:X2}, reject={rejectDetail}");
        PrintIdentity(identity);
    }

    private static void OnBioEvent(IntPtr context, int operationStatus, IntPtr eventPtr)
    {
        bool signal = false;
        try
        {
            if (WinBioApi.Failed(operationStatus))
            {
                Console.WriteLine($"Event callback reported an error ({WinBioApi.Hr(operationStatus)}).");
                signal = true;
            }

            if (eventPtr == IntPtr.Zero)
            {
                return;
            }

            try
            {
                uint type = unchecked((uint)Marshal.ReadInt32(eventPtr));
                if (!WinBioApi.Failed(operationStatus) && type == WinBioApi.EventFpUnclaimedIdentify)
                {
                    eventWasIdentify = true;
                    signal = true;
                    PrintIdentifyEvent(eventPtr);
                }
                else
                {
                    Console.WriteLine($"Event type 0x{type:X8} received; ignored.");
                }
            }
            finally
            {
                // Docs require WinBioFree on every event pointer passed to the callback.
                int freeHr = WinBioApi.WinBioFree(eventPtr);
                if (WinBioApi.Failed(freeHr))
                {
                    Console.WriteLine($"WinBioFree failed ({WinBioApi.Hr(freeHr)}).");
                }
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never escape a native callback.
            Console.Error.WriteLine($"Event callback error: {ex.Message}");
            signal = true;
        }
        finally
        {
            if (signal)
            {
                EventReady.Set();
            }
        }
    }

    private static void PrintIdentifyEvent(IntPtr eventPtr)
    {
        WinBioApi.Event evt = Marshal.PtrToStructure<WinBioApi.Event>(eventPtr);
        byte[] buffer = evt.Parameters.Bytes ?? Array.Empty<byte>();
        if (buffer.Length < EventRequiredSize)
        {
            throw new InvalidOperationException($"Event buffer is {buffer.Length} bytes; expected {EventRequiredSize}.");
        }

        uint unitId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(EventUnitIdOffset, 4));
        var identity = new WinBioApi.Identity
        {
            Type = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(EventIdentityOffset, 4)),
            Value = new WinBioApi.IdentityValue
            {
                Bytes = buffer.AsSpan(EventIdentityOffset + 4, WinBioApi.IdentityValueSize).ToArray(),
            },
        };
        byte subFactor = buffer[EventSubFactorOffset];
        uint rejectDetail = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(EventRejectDetailOffset, 4));

        Console.WriteLine("Event WINBIO_EVENT_FP_UNCLAIMED_IDENTIFY:");
        Console.WriteLine($"  unit={unitId}, subtype=0x{subFactor:X2}, reject={rejectDetail}");
        PrintIdentity(identity);
    }

    private static void PrintIdentity(in WinBioApi.Identity identity)
    {
        byte[] bytes = identity.Value.Bytes ?? Array.Empty<byte>();

        switch ((WinBioApi.IdentityType)identity.Type)
        {
            case WinBioApi.IdentityType.Guid:
                if (bytes.Length < WinBioApi.GuidSize)
                {
                    Console.WriteLine("  Identity: GUID (truncated)");
                    break;
                }

                Console.WriteLine($"  Identity: GUID {new Guid(bytes.AsSpan(0, WinBioApi.GuidSize))}");
                break;

            case WinBioApi.IdentityType.Sid:
                if (bytes.Length < 4)
                {
                    Console.WriteLine("  Identity: SID (truncated)");
                    break;
                }

                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
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
                Console.WriteLine($"  Identity: SecureId {Convert.ToHexString(bytes.AsSpan(0, WinBioApi.MaxSecureIdSize))}");
                break;

            case WinBioApi.IdentityType.Wildcard:
                Console.WriteLine("  Identity: wildcard");
                break;

            default:
                Console.WriteLine("  Identity: none");
                break;
        }
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        EventReady.Set();
    }
}
