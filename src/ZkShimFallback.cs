using System.Text;

namespace WindowsFingerprintSimulator;

internal static class ZkShimFallback
{
    public const string FileName = "zkshim-config.txt";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Best-effort write of the shim's one-line startup message.</summary>
    public static bool WriteAcquire(string idHex, string templateBase64, string? directory = null)
    {
        try
        {
            string path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
            File.WriteAllText(path, ZkShimPipe.BuildAcquireLine(idHex, templateBase64) + "\n", Utf8NoBom);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
