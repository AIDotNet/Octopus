using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Octopus.Core.Agents;
using Octopus.Core.Bots;
using Octopus.Platform;

namespace Octopus.App.Bots;

public enum ConnectorProbeStatus
{
    Reachable,
    AuthRequired,
    Failed,
    InvalidUrl,
}

public sealed record ConnectorProbeResult(
    ConnectorProbeStatus Status,
    int? HttpStatusCode,
    string Message,
    bool SupportsOAuth = false);

/// <summary>
/// Runtime bridge for Bot MCP connectors: stores Bearer tokens securely in <see cref="ISecretStore"/>,
/// builds <see cref="AcpMcpServerConfig"/> entries for ACP GUI sessions, and probes Remote MCP endpoints.
/// </summary>
public sealed class BotConnectorRuntime
{
    public const string SecretService = "Octopus/bots";
    private const string InitializeJson =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"Octopus","version":"1.0"}}}""";

    private static readonly HttpClient SharedHttpClient = CreateDefaultHttpClient();

    private readonly ISecretStore? _secrets;
    private readonly HttpClient _http;

    public BotConnectorRuntime(ISecretStore? secrets = null, HttpClient? httpClient = null)
    {
        _secrets = secrets;
        _http = httpClient ?? SharedHttpClient;
    }

    public static string SecretName(string botId, string connectorId) => $"{botId}:{connectorId}";

    public bool IsSecretStoreAvailable => _secrets is { IsAvailable: true };

    public async Task SetTokenAsync(string botId, string connectorId, string? token, CancellationToken ct = default)
    {
        if (_secrets is not { IsAvailable: true }) return;
        var name = SecretName(botId, connectorId);
        if (string.IsNullOrWhiteSpace(token))
        {
            await _secrets.DeleteAsync(SecretService, name, ct).ConfigureAwait(false);
        }
        else
        {
            await _secrets.SetAsync(SecretService, name, token.Trim(), ct).ConfigureAwait(false);
        }
    }

    public async Task<string?> GetTokenAsync(string botId, string connectorId, CancellationToken ct = default)
    {
        if (_secrets is not { IsAvailable: true }) return null;
        try
        {
            return await _secrets.GetAsync(SecretService, SecretName(botId, connectorId), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task DeleteTokenAsync(string botId, string connectorId, CancellationToken ct = default)
    {
        if (_secrets is not { IsAvailable: true }) return;
        try
        {
            await _secrets.DeleteAsync(SecretService, SecretName(botId, connectorId), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Builds <see cref="AcpMcpServerConfig"/> definitions for every enabled Remote MCP connector on <paramref name="bot"/>.</summary>
    public async Task<IReadOnlyList<AcpMcpServerConfig>> BuildAcpMcpServersAsync(BotDefinition bot, CancellationToken ct = default)
    {
        var result = new List<AcpMcpServerConfig>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var conn in bot.Connectors)
        {
            if (!conn.Enabled || string.IsNullOrWhiteSpace(conn.Url)) continue;
            if (!TryValidateHttpUrl(conn.Url, out var uri)) continue;

            var baseName = BotCatalog.NormalizeMcpServerName(
                string.Equals(conn.AppId, "custom-url", StringComparison.OrdinalIgnoreCase)
                    ? conn.DisplayName
                    : conn.AppId,
                fallback: "connector");
            var name = EnsureUniqueName(baseName, usedNames);

            var transport = string.Equals(conn.Transport, "sse", StringComparison.OrdinalIgnoreCase)
                ? "sse"
                : "http";

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (conn.HasSecret || string.Equals(conn.AuthMode, "bearer", StringComparison.OrdinalIgnoreCase))
            {
                var token = await GetTokenAsync(bot.Id, conn.Id, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    headers["Authorization"] = FormatAuthorizationHeader(token);
                }
            }

            result.Add(new AcpMcpServerConfig
            {
                Name = name,
                Transport = transport,
                Url = uri.AbsoluteUri,
                Headers = headers,
                Enabled = true,
            });
        }

        return result;
    }

    /// <summary>Merges global ACP MCP servers with Bot-specific servers (Bot servers win on duplicate name).</summary>
    public static IReadOnlyList<AcpMcpServerConfig> MergeMcpServers(
        IReadOnlyList<AcpMcpServerConfig>? globalServers,
        IReadOnlyList<AcpMcpServerConfig>? botServers)
    {
        if (botServers is null || botServers.Count == 0)
        {
            return globalServers ?? [];
        }
        if (globalServers is null || globalServers.Count == 0)
        {
            return botServers;
        }

        var merged = new List<AcpMcpServerConfig>(globalServers.Count + botServers.Count);
        var botNames = new HashSet<string>(
            botServers.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var s in globalServers)
        {
            if (!botNames.Contains(s.Name))
            {
                merged.Add(s);
            }
        }
        merged.AddRange(botServers);
        return merged;
    }

    /// <summary>Probes a Remote MCP HTTP/SSE endpoint to verify reachability and detect OAuth / Bearer auth requirements.</summary>
    public async Task<ConnectorProbeResult> ProbeAsync(
        string? url,
        string transport = "http",
        string? bearerToken = null,
        CancellationToken ct = default)
    {
        if (!TryValidateHttpUrl(url, out var uri))
        {
            return new ConnectorProbeResult(ConnectorProbeStatus.InvalidUrl, null, "URL must start with http:// or https://");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

        var isSse = string.Equals(transport, "sse", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var response = await SendProbeRequestAsync(uri, isSse ? HttpMethod.Get : HttpMethod.Post, isSse, bearerToken, timeoutCts.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.MethodNotAllowed && !isSse)
            {
                using var getResponse = await SendProbeRequestAsync(uri, HttpMethod.Get, isSse: true, bearerToken, timeoutCts.Token).ConfigureAwait(false);
                return InterpretProbeResponse(getResponse);
            }

            return InterpretProbeResponse(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ConnectorProbeResult(ConnectorProbeStatus.Failed, null, "Connection timed out");
        }
        catch (HttpRequestException ex)
        {
            return new ConnectorProbeResult(ConnectorProbeStatus.Failed, (int?)ex.StatusCode, ex.Message);
        }
    }

    private async Task<HttpResponseMessage> SendProbeRequestAsync(
        Uri uri,
        HttpMethod method,
        bool isSse,
        string? bearerToken,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, uri);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            req.Headers.TryAddWithoutValidation("Authorization", FormatAuthorizationHeader(bearerToken));
        }
        if (method == HttpMethod.Post && !isSse)
        {
            req.Content = new StringContent(InitializeJson, Encoding.UTF8, "application/json");
        }
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static ConnectorProbeResult InterpretProbeResponse(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return new ConnectorProbeResult(ConnectorProbeStatus.Reachable, code, $"HTTP {code}");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var wwwAuth = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
            var supportsOAuth = wwwAuth.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
                || wwwAuth.Contains("resource_metadata", StringComparison.OrdinalIgnoreCase);
            return new ConnectorProbeResult(ConnectorProbeStatus.AuthRequired, code, $"HTTP {code}", supportsOAuth);
        }
        return new ConnectorProbeResult(ConnectorProbeStatus.Failed, code, $"HTTP {code} {response.ReasonPhrase}");
    }

    public static bool TryValidateHttpUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        uri = parsed;
        return true;
    }

    internal static string FormatAuthorizationHeader(string token)
    {
        var trimmed = token.Trim();
        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"Bearer {trimmed}";
    }

    internal static string EnsureUniqueName(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Octopus-Bots/1.0");
        return client;
    }
}
