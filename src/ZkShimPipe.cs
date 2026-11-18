using System.IO.Pipes;
using System.Text;

namespace WindowsFingerprintSimulator;

/// <summary>Named-pipe endpoint used by the in-MER ZKTeco shim.</summary>
internal sealed class ZkShimPipe : IDisposable
{
    public const string PipeName = "wfp-zk-shim";
    private const string AcquireKeyword = "ACQUIRE";
    private const int RetryDelayMs = 100;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly Action<string> _onAcquire;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _pendingLock = new();
    private volatile NamedPipeServerStream? _activeServer;
    private string _pendingIdHex = string.Empty;
    private string _pendingTemplate = string.Empty;
    private bool _disposed;

    public ZkShimPipe(Action<string> onAcquire)
    {
        _onAcquire = onAcquire;
        _ = ServeAsync(_cts.Token);
    }

    public static string BuildAcquireLine(string idHex, string templateBase64) =>
        $"{AcquireKeyword} {idHex} {templateBase64}";

    /// <summary>Stages the template served to the shim on its next ACQUIRE request.</summary>
    public void SetPending(string idHex, string framedTemplateBase64)
    {
        lock (_pendingLock)
        {
            _pendingIdHex = idHex;
            _pendingTemplate = framedTemplateBase64;
        }
    }

    private (string IdHex, string TemplateBase64)? ReadPending()
    {
        lock (_pendingLock)
        {
            return _pendingTemplate.Length == 0 ? null : (_pendingIdHex, _pendingTemplate);
        }
    }

    private async Task ServeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (Exception)
            {
                // Back off on persistent pipe errors instead of spinning.
                try { await Task.Delay(RetryDelayMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                continue;
            }

            await using (server)
            {
                _activeServer = server;
                try
                {
                    try
                    {
                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch (IOException) { continue; }

                    try
                    {
                        HandleConnection(server);
                    }
                    catch (Exception)
                    {
                        // A shim disconnecting mid-line must not stop future connections.
                    }
                }
                finally
                {
                    _activeServer = null;
                }
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream server)
    {
        using var reader = new StreamReader(server, Utf8NoBom, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, Utf8NoBom, 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        if (reader.ReadLine() != AcquireKeyword)
        {
            return;
        }

        (string IdHex, string TemplateBase64)? pending = ReadPending();
        if (pending is null)
        {
            return;
        }

        writer.WriteLine(BuildAcquireLine(pending.Value.IdHex, pending.Value.TemplateBase64));
        if (reader.ReadLine() == "OK")
        {
            _onAcquire(pending.Value.IdHex);
        }
    }

    internal static void SelfCheck()
    {
        if (BuildAcquireLine("ABCD", "AAAA") != "ACQUIRE ABCD AAAA")
        {
            throw new InvalidOperationException("ZkShimPipe self-check failed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        _activeServer?.Dispose();
        _cts.Dispose();
    }
}
