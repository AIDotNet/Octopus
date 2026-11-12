using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Octopus.Agents.Signals;

/// <summary>The user's own command in a slot that holds only one, plus the status line options worth keeping.</summary>
/// <param name="Shell">A command line the agent would run through a shell.</param>
/// <param name="Argv">A program and its arguments, run without a shell (Codex <c>notify</c>).</param>
internal sealed record UserSlotCommand(string? Shell, IReadOnlyList<string>? Argv, int? Padding = null, int? RefreshInterval = null, bool? HideVimModeIndicator = null)
{
    /// <summary>The value of <c>OCTOPUS_SIGNAL_CHAIN</c>, in the form the forwarder's <c>ChainCommand</c> reads.</summary>
    public string ToChainJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        // Relaxed escaping keeps a command such as echo 'x' readable in the environment; it is still valid JSON.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            if (Shell is not null)
            {
                writer.WriteString("shell", Shell);
            }
            else
            {
                writer.WriteStartArray("argv");
                foreach (var arg in Argv ?? [])
                {
                    writer.WriteStringValue(arg);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// Finds, at launch, the command the user already put in a single slot that Octopus is about to take over, so the
/// forwarder can run it after forwarding (design/plugins/11 §1.4). Read-only: these files are opened, the one key is
/// taken, and nothing else from them is kept. Credential files are never opened.
/// </summary>
internal static class UserSlotCommands
{
    /// <summary>Settings files are small; a huge one is not read.</summary>
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Claude Code's effective <c>statusLine</c> among the user, project and local settings: local
    /// (<c>.claude/settings.local.json</c>) beats project (<c>.claude/settings.json</c>) beats the account's
    /// <c>settings.json</c>. Managed settings and <c>--settings</c> files are outside this lookup.
    /// </summary>
    public static UserSlotCommand? ClaudeStatusLine(string accountDirectory, string? workingDirectory)
    {
        var files = new List<string>();
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            files.Add(Path.Combine(workingDirectory, ".claude", "settings.local.json"));
            files.Add(Path.Combine(workingDirectory, ".claude", "settings.json"));
        }
        if (!string.IsNullOrEmpty(accountDirectory))
        {
            files.Add(Path.Combine(accountDirectory, "settings.json"));
        }
        foreach (var file in files)
        {
            var text = ReadSmallFile(file);
            if (text is null)
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(text, JsonOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("statusLine", out var statusLine)
                    || statusLine.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                // The highest layer that sets statusLine decides, even if it is not a command we can chain.
                if (statusLine.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "command")
                {
                    return null;
                }
                if (!statusLine.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(command.GetString()))
                {
                    return null;
                }
                return new UserSlotCommand(
                    command.GetString(),
                    null,
                    Int(statusLine, "padding"),
                    Int(statusLine, "refreshInterval"),
                    statusLine.TryGetProperty("hideVimModeIndicator", out var hide) && hide.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? hide.GetBoolean()
                        : null);
            }
            catch (JsonException)
            {
                // A file Claude itself would reject sets nothing.
            }
        }
        return null;
    }

    /// <summary>Codex's top-level <c>notify</c> array in <c>config.toml</c> of the account directory.</summary>
    public static UserSlotCommand? CodexNotify(string accountDirectory)
    {
        var values = ReadToml(Path.Combine(accountDirectory, "config.toml"));
        if (values is null || !values.TryGetValue("notify", out var notify) || notify is not List<object?> items)
        {
            return null;
        }
        var argv = items.OfType<string>().ToList();
        return argv.Count == items.Count && argv.Count > 0 && argv[0].Length > 0 ? new UserSlotCommand(null, argv) : null;
    }

    /// <summary>Grok's <c>[ui.status_line]</c> when its type is <c>command</c>.</summary>
    public static UserSlotCommand? GrokStatusLine(string accountDirectory)
    {
        var values = ReadToml(Path.Combine(accountDirectory, "config.toml"));
        if (values is null
            || !values.TryGetValue("ui.status_line.type", out var type) || type is not "command"
            || !values.TryGetValue("ui.status_line.command", out var command) || command is not string { Length: > 0 } shell)
        {
            return null;
        }
        var padding = values.TryGetValue("ui.status_line.padding", out var p) && p is MiniToml.Raw raw && int.TryParse(raw.Text, out var n) ? n : (int?)null;
        return new UserSlotCommand(shell, null, padding);
    }

    private static Dictionary<string, object?>? ReadToml(string file)
    {
        var text = ReadSmallFile(file);
        return text is null ? null : MiniToml.Parse(text);
    }

    private static string? ReadSmallFile(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
}
