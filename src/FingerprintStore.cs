using System.Text.Json;

namespace WindowsFingerprintSimulator;

internal sealed class FingerprintRecord
{
    public string Name { get; set; } = string.Empty;
    public string IdHex { get; set; } = string.Empty;
    public string TemplateBase64 { get; set; } = string.Empty;
}

internal static class FingerprintStore
{
    private static readonly string StorePath = System.IO.Path.Combine(AppContext.BaseDirectory, "fingerprints.json");

    public static List<FingerprintRecord> Load()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return new();
            }

            var parsed = JsonSerializer.Deserialize<List<FingerprintRecord>>(File.ReadAllText(StorePath));
            return parsed is null ? new() : parsed.Where(IsValid).ToList();
        }
        catch
        {
            // Missing, corrupt, or unreadable file must never block startup.
            return new();
        }
    }

    public static void Save(IReadOnlyList<FingerprintRecord> records)
    {
        string temp = StorePath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, StorePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort cleanup */ }
            }
        }
    }

    /// <summary>Deterministic round-trip check for the JSON parsing/validation used at startup.</summary>
    public static void SelfCheck()
    {
        var sample = new FingerprintRecord
        {
            Name = "self-check",
            IdHex = Convert.ToHexString(new byte[16]),
            TemplateBase64 = Convert.ToBase64String(new byte[84]),
        };

        var parsed = JsonSerializer.Deserialize<List<FingerprintRecord>>(JsonSerializer.Serialize(new[] { sample }));
        if (parsed is null || parsed.Count != 1 || !IsValid(parsed[0]) ||
            parsed[0].Name != sample.Name || parsed[0].IdHex != sample.IdHex ||
            parsed[0].TemplateBase64 != sample.TemplateBase64 || IsValid(new FingerprintRecord()))
        {
            throw new InvalidOperationException("FingerprintStore self-check failed: JSON round-trip or validation is incorrect.");
        }
    }

    private static bool IsValid(FingerprintRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Name) ||
            string.IsNullOrEmpty(record.IdHex) || record.IdHex.Length != 32 || !record.IdHex.All(Uri.IsHexDigit) ||
            string.IsNullOrEmpty(record.TemplateBase64))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(record.TemplateBase64).Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
