using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Octopus.Core.Bots;
using Octopus.Storage;

namespace Octopus.App.Bots;

/// <summary>
/// Encodes and decodes portable <see cref="BotSharePayload"/> snapshots into <c>octopus://bot/import?data=...</c> deep links.
/// Uses GZip + Base64Url and strict size limits; never includes secrets.
/// </summary>
public static class BotShareCodec
{
    public const string ImportUriPrefix = "octopus://bot/import?data=";
    public const int MaxDecompressedBytes = 64 * 1024;
    public const int MaxEncodedChars = 32 * 1024;

    /// <summary>Builds a portable <see cref="BotSharePayload"/> from a Bot definition and its home directory files.</summary>
    public static BotSharePayload BuildPayload(BotDefinition bot, BotHomeManager home, bool includeMemory)
    {
        var soul = home.ReadSoul(bot);
        string? memory = null;
        if (includeMemory)
        {
            var memPath = home.GetMemoryPath(bot);
            try
            {
                if (File.Exists(memPath))
                {
                    memory = File.ReadAllText(memPath, Encoding.UTF8).Trim();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new BotSharePayload
        {
            Version = 1,
            Name = bot.Name,
            AgentId = bot.AgentId,
            Color = bot.Color,
            Icon = bot.Icon,
            TemplateId = bot.TemplateId,
            PermissionMode = bot.PermissionMode,
            LookAroundEnabled = bot.LookAroundEnabled,
            LookAroundIntervalHours = bot.LookAroundIntervalHours,
            TidyUpMemoryEnabled = bot.TidyUpMemoryEnabled,
            StaleAfterDays = bot.StaleAfterDays,
            SetupNotes = bot.SetupNotes,
            SoulMarkdown = string.IsNullOrWhiteSpace(soul) ? null : soul,
            MemoryMarkdown = string.IsNullOrWhiteSpace(memory) ? null : memory,
            Connectors =
            [
                .. bot.Connectors
                    .Where(c => c.Enabled)
                    .Select(c => new BotShareConnectorDto
                    {
                        AppId = c.AppId,
                        DisplayName = c.DisplayName,
                        Url = c.Url,
                        Transport = c.Transport,
                        AuthMode = c.AuthMode,
                        VariableValue = c.VariableValue,
                    }),
            ],
        };
    }

    /// <summary>Encodes <paramref name="payload"/> into a clickable <c>octopus://bot/import?data=...</c> URI string.</summary>
    public static string Encode(BotSharePayload payload)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, OctopusJsonContext.FileTypeInfo<BotSharePayload>());
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(jsonBytes, 0, jsonBytes.Length);
        }
        return ImportUriPrefix + ToBase64Url(output.ToArray());
    }

    /// <summary>Returns true if <paramref name="uri"/> targets <c>octopus://bot/import</c>.</summary>
    public static bool IsBotImportUri(Uri? uri) =>
        uri is not null
        && string.Equals(uri.Scheme, "octopus", StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "bot", StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.AbsolutePath.Trim('/'), "import", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes an <c>octopus://bot/import?data=...</c> URI into a validated <see cref="BotSharePayload"/>.</summary>
    public static bool TryDecode(Uri? uri, out BotSharePayload? payload, out string? error)
    {
        payload = null;
        error = null;
        if (!IsBotImportUri(uri) || uri is null)
        {
            error = "Not an octopus://bot/import link.";
            return false;
        }

        var encoded = ExtractQueryParam(uri.Query, "data");
        if (string.IsNullOrWhiteSpace(encoded))
        {
            error = "Missing data parameter.";
            return false;
        }

        if (encoded.Length > MaxEncodedChars)
        {
            error = "Share link exceeds maximum allowed size.";
            return false;
        }

        try
        {
            var compressed = FromBase64Url(encoded);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decompressed.Length + read > MaxDecompressedBytes)
                {
                    error = "Decompressed Bot payload exceeds safety limit.";
                    return false;
                }
                decompressed.Write(buffer, 0, read);
            }

            var parsed = JsonSerializer.Deserialize(decompressed.ToArray(), OctopusJsonContext.FileTypeInfo<BotSharePayload>());
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Name))
            {
                error = "Invalid Bot payload.";
                return false;
            }
            payload = parsed;
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or JsonException)
        {
            error = e.Message;
            return false;
        }
    }

    public static bool TryDecodeString(string? text, out BotSharePayload? payload, out string? error)
    {
        payload = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Invalid URI.";
            return false;
        }
        return TryDecode(uri, out payload, out error);
    }

    /// <summary>Scans a decoded payload for obvious secret/token prefixes before importing.</summary>
    public static IReadOnlyList<string> ScanPayloadForSecrets(BotSharePayload payload)
    {
        var findings = new List<string>();
        BotHomeManager.ScanTextForSecrets("SOUL.md", payload.SoulMarkdown ?? "", findings);
        BotHomeManager.ScanTextForSecrets("MEMORY.md", payload.MemoryMarkdown ?? "", findings);
        BotHomeManager.ScanTextForSecrets("SetupNotes", payload.SetupNotes ?? "", findings);
        return findings;
    }

    private static string? ExtractQueryParam(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var span = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in span.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = Uri.UnescapeDataString(part[..eq]);
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }
        return null;
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var mod = padded.Length % 4;
        if (mod == 2) padded += "==";
        else if (mod == 3) padded += "=";
        else if (mod == 1) throw new FormatException("Invalid base64url length.");
        return Convert.FromBase64String(padded);
    }
}
