using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Octopus.SignalForwarder;

/// <summary>
/// Minimal NativeAOT MCP stdio-to-loopback-HTTP bridge invoked via <c>octopus-signal --mcp</c>.
/// Allows stdio-only ACP adapters and terminal agent CLIs (Claude Code, Codex CLI, Antigravity CLI, etc.)
/// to talk to Octopus's in-process local collaboration MCP gateway using per-session bearer authentication.
/// </summary>
internal static class McpStdioBridge
{
    public const string UrlVariable = "OCTOPUS_COLLAB_URL";
    public const string TokenVariable = "OCTOPUS_COLLAB_TOKEN";
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    public static bool IsMcpInvocation(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], "--mcp", StringComparison.Ordinal);

    internal static SocketsHttpHandler CreateDefaultHandler() =>
        new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
        };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        Stream stdin,
        Stream stdout,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        if (HasDisallowedTokenArgument(args))
        {
            return 1;
        }

        var url = ResolveArgOrEnv(args, "--url", environment, UrlVariable);
        var token = environment.TryGetValue(TokenVariable, out var envToken) ? envToken : null;

        if (string.IsNullOrWhiteSpace(url)
            || string.IsNullOrWhiteSpace(token)
            || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !endpoint.IsLoopback)
        {
            return 1;
        }

        using var http = handler is null
            ? new HttpClient(CreateDefaultHandler()) { Timeout = RequestTimeout }
            : new HttpClient(handler, disposeHandler: false) { Timeout = RequestTimeout };

        using var reader = new StreamReader(stdin, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
        var writeGate = new SemaphoreSlim(1, 1);

        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = await ReadNextMessageAsync(reader, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            var response = await ForwardRequestAsync(http, endpoint, token, payload, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response))
            {
                var line = NormalizeSingleLine(response) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await stdout.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }

        return 0;
    }

    private static async Task<string?> ReadNextMessageAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Support optional LSP-style Content-Length framing in addition to standard MCP NDJSON lines.
            if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(trimmed["Content-Length:".Length..].Trim(), out var contentLength)
                    || contentLength <= 0 || contentLength > MaxMessageBytes)
                {
                    return "";
                }
                // Consume remaining headers until the blank line separator.
                while (true)
                {
                    var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (headerLine is null) return null;
                    if (headerLine.Length == 0) break;
                }
                var chars = new char[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var n = await reader.ReadAsync(chars.AsMemory(read, contentLength - read), cancellationToken).ConfigureAwait(false);
                    if (n <= 0) return null;
                    read += n;
                }
                return new string(chars, 0, read);
            }

            if (Encoding.UTF8.GetByteCount(trimmed) > MaxMessageBytes)
            {
                return "";
            }
            return trimmed;
        }
    }

    private static async Task<string?> ForwardRequestAsync(
        HttpClient http,
        Uri endpoint,
        string? token,
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.Accepted || res.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }
            var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                return string.IsNullOrWhiteSpace(body) ? null : body;
            }
            return BuildErrorResponseIfRequest(requestJson, -32000, $"Octopus collaboration gateway returned HTTP {(int)res.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return BuildErrorResponseIfRequest(requestJson, -32000, $"Octopus collaboration gateway unreachable: {ex.Message}");
        }
    }

    private static string? BuildErrorResponseIfRequest(string requestJson, int code, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("id", out var idProp))
            {
                // Notification (no id): never emit a JSON-RPC response.
                return null;
            }
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                idProp.WriteTo(writer);
                writer.WriteStartObject("error");
                writer.WriteNumber("code", code);
                writer.WriteString("message", message);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeSingleLine(string text)
    {
        if (!text.Contains('\n') && !text.Contains('\r'))
        {
            return text;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        }
    }

    private static bool HasDisallowedTokenArgument(IReadOnlyList<string> args)
    {
        for (var i = 1; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--token", StringComparison.OrdinalIgnoreCase)
                || args[i].StartsWith("--token=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ResolveArgOrEnv(
        IReadOnlyList<string> args,
        string flag,
        IReadOnlyDictionary<string, string> env,
        string envName)
    {
        for (var i = 1; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return env.TryGetValue(envName, out var value) ? value : null;
    }
}
