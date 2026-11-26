using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Octopus.App.Bots;

/// <summary>Serialized OAuth 2.1 credential stored securely in <c>ISecretStore</c> for a Remote MCP connector.</summary>
public sealed record BotOAuthCredential(
    string AccessToken,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null,
    string? TokenEndpoint = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? TokenEndpointAuthMethod = null,
    string? Resource = null,
    string? Scope = null)
{
    private const string KindValue = "oauth";

    public bool IsExpiringSoon(TimeSpan? skew = null) =>
        ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow.Add(skew ?? TimeSpan.FromSeconds(60));

    public bool CanRefresh =>
        !string.IsNullOrWhiteSpace(RefreshToken)
        && !string.IsNullOrWhiteSpace(TokenEndpoint)
        && !string.IsNullOrWhiteSpace(ClientId);

    public string Serialize()
    {
        var obj = new JsonObject
        {
            ["v"] = 1,
            ["kind"] = KindValue,
            ["access_token"] = AccessToken,
        };
        if (!string.IsNullOrWhiteSpace(RefreshToken)) obj["refresh_token"] = RefreshToken;
        if (ExpiresAt is { } exp) obj["expires_at"] = exp.ToUnixTimeSeconds();
        if (!string.IsNullOrWhiteSpace(TokenEndpoint)) obj["token_endpoint"] = TokenEndpoint;
        if (!string.IsNullOrWhiteSpace(ClientId)) obj["client_id"] = ClientId;
        if (!string.IsNullOrWhiteSpace(ClientSecret)) obj["client_secret"] = ClientSecret;
        if (!string.IsNullOrWhiteSpace(TokenEndpointAuthMethod)) obj["token_endpoint_auth_method"] = TokenEndpointAuthMethod;
        if (!string.IsNullOrWhiteSpace(Resource)) obj["resource"] = Resource;
        if (!string.IsNullOrWhiteSpace(Scope)) obj["scope"] = Scope;
        return obj.ToJsonString();
    }

    public static bool TryDeserialize(string? raw, out BotOAuthCredential? credential)
    {
        credential = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('{')) return false;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!TryGetString(root, "kind", out var kind) || !string.Equals(kind, KindValue, StringComparison.Ordinal))
            {
                return false;
            }
            if (!TryGetString(root, "access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
            {
                return false;
            }

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("expires_at", out var expEl) && expEl.ValueKind == JsonValueKind.Number && expEl.TryGetInt64(out var unix))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            }

            credential = new BotOAuthCredential(
                AccessToken: accessToken,
                RefreshToken: TryGetString(root, "refresh_token", out var rt) ? rt : null,
                ExpiresAt: expiresAt,
                TokenEndpoint: TryGetString(root, "token_endpoint", out var te) ? te : null,
                ClientId: TryGetString(root, "client_id", out var cid) ? cid : null,
                ClientSecret: TryGetString(root, "client_secret", out var cs) ? cs : null,
                TokenEndpointAuthMethod: TryGetString(root, "token_endpoint_auth_method", out var am) ? am : null,
                Resource: TryGetString(root, "resource", out var res) ? res : null,
                Scope: TryGetString(root, "scope", out var sc) ? sc : null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString();
            return true;
        }
        value = null;
        return false;
    }
}

public sealed record BotMcpOAuthMetadata(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    string? Resource,
    string? Scope,
    IReadOnlyList<string> TokenEndpointAuthMethods);

public sealed record BotMcpOAuthResult(
    bool Succeeded,
    BotOAuthCredential? Credential = null,
    string? Error = null,
    bool Cancelled = false);

/// <summary>
/// Implements the Remote MCP OAuth 2.1 authorization flow:
/// RFC 9728 Protected Resource Metadata discovery, RFC 8414 Authorization Server Metadata discovery,
/// RFC 7591 Dynamic Client Registration, PKCE S256, loopback callback server, token exchange, and token refresh.
/// </summary>
public sealed partial class BotMcpOAuthClient
{
    private const string ClientDisplayName = "Octopus";
    private const string DiscoveryInitializeJson =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"Octopus","version":"1.0"}}}""";
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly Func<string, bool> _openUrl;

    public BotMcpOAuthClient(HttpClient httpClient, Func<string, bool> openUrl)
    {
        _http = httpClient;
        _openUrl = openUrl;
    }

    /// <summary>Discovers OAuth 2.1 endpoints for a Remote MCP server URL.</summary>
    public async Task<BotMcpOAuthMetadata?> DiscoverAsync(Uri mcpUri, CancellationToken ct = default)
    {
        // Step 1: Probe endpoint without credentials to read WWW-Authenticate hints (RFC 9728).
        // Some servers (e.g. Feishu, Tencent Docs) return 404/405 on GET and only advertise
        // WWW-Authenticate on Streamable HTTP POST initialize requests.
        var (wwwResourceMetadataUrl, wwwScope) = await ProbeAuthChallengeAsync(mcpUri, HttpMethod.Get, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wwwResourceMetadataUrl))
        {
            var (postMetaUrl, postScope) = await ProbeAuthChallengeAsync(mcpUri, HttpMethod.Post, ct).ConfigureAwait(false);
            wwwResourceMetadataUrl ??= postMetaUrl;
            wwwScope ??= postScope;
        }

        // Step 2: Fetch Protected Resource Metadata (RFC 9728).
        Uri? authServerBase = null;
        string? resource = null;
        string? resourceScope = null;

        foreach (var prmUrl in BuildProtectedResourceCandidates(mcpUri, wwwResourceMetadataUrl))
        {
            var prm = await TryGetJsonAsync(prmUrl, ct).ConfigureAwait(false);
            if (prm is null) continue;
            using (prm)
            {
                var root = prm.RootElement;
                if (root.TryGetProperty("authorization_servers", out var serversEl) && serversEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in serversEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String
                            && BotConnectorRuntime.TryValidateHttpUrl(item.GetString(), out var parsed))
                        {
                            authServerBase = parsed;
                            break;
                        }
                    }
                }
                if (root.TryGetProperty("resource", out var resEl) && resEl.ValueKind == JsonValueKind.String)
                {
                    var val = resEl.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) resource = val.Trim();
                }
                if (root.TryGetProperty("scopes_supported", out var scopesEl) && scopesEl.ValueKind == JsonValueKind.Array)
                {
                    var scopes = scopesEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                        .Select(e => e.GetString()!.Trim())
                        .ToList();
                    if (scopes.Count > 0)
                    {
                        resourceScope = string.Join(' ', scopes);
                    }
                }
                if (authServerBase is not null || resource is not null)
                {
                    break;
                }
            }
        }

        // Step 3: Fetch Authorization Server Metadata (RFC 8414 / OIDC Discovery).
        var issuerBase = authServerBase ?? new Uri(mcpUri.GetLeftPart(UriPartial.Authority));
        foreach (var asUrl in BuildAuthServerMetadataCandidates(issuerBase))
        {
            var doc = await TryGetJsonAsync(asUrl, ct).ConfigureAwait(false);
            if (doc is null) continue;
            using (doc)
            {
                var root = doc.RootElement;
                var authEndpoint = GetOptionalString(root, "authorization_endpoint");
                var tokenEndpoint = GetOptionalString(root, "token_endpoint");
                if (!BotConnectorRuntime.TryValidateHttpUrl(authEndpoint, out _)
                    || !BotConnectorRuntime.TryValidateHttpUrl(tokenEndpoint, out _))
                {
                    continue;
                }

                var registrationEndpoint = GetOptionalString(root, "registration_endpoint");
                if (!BotConnectorRuntime.TryValidateHttpUrl(registrationEndpoint, out _))
                {
                    registrationEndpoint = null;
                }

                var authMethods = new List<string>();
                if (root.TryGetProperty("token_endpoint_auth_methods_supported", out var methodsEl) && methodsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in methodsEl.EnumerateArray())
                    {
                        if (m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                        {
                            authMethods.Add(m.GetString()!.Trim());
                        }
                    }
                }

                // Prefer resource advertised by PRM (or AS metadata if present).
                resource ??= GetOptionalString(root, "resource");
                var scope = !string.IsNullOrWhiteSpace(wwwScope) ? wwwScope : resourceScope;

                return new BotMcpOAuthMetadata(
                    AuthorizationEndpoint: authEndpoint!,
                    TokenEndpoint: tokenEndpoint!,
                    RegistrationEndpoint: registrationEndpoint,
                    Resource: resource,
                    Scope: scope,
                    TokenEndpointAuthMethods: authMethods);
            }
        }

        return null;
    }

    /// <summary>Executes the interactive browser OAuth 2.1 + PKCE sign-in flow for <paramref name="mcpUrl"/>.</summary>
    public async Task<BotMcpOAuthResult> SignInAsync(string mcpUrl, CancellationToken ct = default)
    {
        if (!BotConnectorRuntime.TryValidateHttpUrl(mcpUrl, out var mcpUri))
        {
            return new BotMcpOAuthResult(false, Error: "Invalid MCP URL.");
        }

        BotMcpOAuthMetadata? meta;
        try
        {
            meta = await DiscoverAsync(mcpUri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BotMcpOAuthResult(false, Cancelled: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new BotMcpOAuthResult(false, Error: ex.Message);
        }

        if (meta is null)
        {
            return new BotMcpOAuthResult(false, Error: "Server does not advertise OAuth 2.1 metadata. Configure a Bearer Token instead.");
        }

        if (string.IsNullOrWhiteSpace(meta.RegistrationEndpoint))
        {
            return new BotMcpOAuthResult(false, Error: "Server does not support Dynamic Client Registration (RFC 7591). Configure a Bearer Token instead.");
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            return new BotMcpOAuthResult(false, Error: $"Failed to start local OAuth callback listener: {ex.Message}");
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        // Step 4: RFC 7591 Dynamic Client Registration
        RegisteredClient? registered;
        try
        {
            registered = await RegisterClientAsync(meta, redirectUri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BotMcpOAuthResult(false, Cancelled: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new BotMcpOAuthResult(false, Error: $"OAuth client registration failed: {ex.Message}");
        }

        if (registered is null || string.IsNullOrWhiteSpace(registered.ClientId))
        {
            return new BotMcpOAuthResult(false, Error: "OAuth dynamic client registration did not return a client_id.");
        }

        // Step 5: PKCE S256 + state + authorization URL
        var codeVerifier = GenerateBase64UrlToken(32);
        var codeChallenge = ComputeS256Challenge(codeVerifier);
        var state = GenerateBase64UrlToken(24);
        var authUrl = BuildAuthorizationUrl(meta, registered.ClientId, redirectUri, codeChallenge, state);

        // Start waiting on loopback callback before opening the browser so immediate redirects never race.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(CallbackTimeout);
        var callbackTask = WaitForCallbackAsync(listener, state, waitCts.Token);

        if (!_openUrl(authUrl))
        {
            waitCts.Cancel();
            return new BotMcpOAuthResult(false, Error: "Failed to open system browser for OAuth sign-in.");
        }

        CallbackOutcome outcome;
        try
        {
            outcome = await callbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BotMcpOAuthResult(false, Cancelled: true);
        }
        catch (OperationCanceledException)
        {
            return new BotMcpOAuthResult(false, Error: "OAuth sign-in timed out waiting for browser authorization.");
        }

        if (!string.IsNullOrWhiteSpace(outcome.Error))
        {
            return new BotMcpOAuthResult(false, Error: outcome.Error);
        }
        if (string.IsNullOrWhiteSpace(outcome.Code))
        {
            return new BotMcpOAuthResult(false, Error: "Authorization callback did not contain an authorization code.");
        }

        // Step 6: Exchange authorization code for tokens.
        try
        {
            var credential = await ExchangeCodeAsync(meta, registered, outcome.Code, redirectUri, codeVerifier, ct).ConfigureAwait(false);
            if (credential is null)
            {
                return new BotMcpOAuthResult(false, Error: "OAuth token endpoint did not return an access_token.");
            }
            return new BotMcpOAuthResult(true, Credential: credential);
        }
        catch (OperationCanceledException)
        {
            return new BotMcpOAuthResult(false, Cancelled: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return new BotMcpOAuthResult(false, Error: ex.Message);
        }
    }

    /// <summary>Refreshes an expired <see cref="BotOAuthCredential"/> using its refresh_token.</summary>
    public async Task<BotOAuthCredential?> RefreshAsync(BotOAuthCredential credential, CancellationToken ct = default)
    {
        if (!credential.CanRefresh) return null;
        if (!BotConnectorRuntime.TryValidateHttpUrl(credential.TokenEndpoint, out var tokenUri)) return null;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", credential.RefreshToken!),
            new("client_id", credential.ClientId!),
        };
        if (!string.IsNullOrWhiteSpace(credential.Resource))
        {
            fields.Add(new("resource", credential.Resource));
        }

        using var req = CreateTokenRequest(tokenUri, fields, credential.ClientId!, credential.ClientSecret, credential.TokenEndpointAuthMethod);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        return ParseTokenResponse(
            body,
            credential.TokenEndpoint!,
            credential.ClientId!,
            credential.ClientSecret,
            credential.TokenEndpointAuthMethod,
            credential.Resource,
            fallbackRefreshToken: credential.RefreshToken,
            fallbackScope: credential.Scope);
    }

    private async Task<RegisteredClient?> RegisterClientAsync(BotMcpOAuthMetadata meta, string redirectUri, CancellationToken ct)
    {
        var preferredAuthMethod = meta.TokenEndpointAuthMethods.Count == 0
            || meta.TokenEndpointAuthMethods.Contains("none", StringComparer.OrdinalIgnoreCase)
                ? "none"
                : meta.TokenEndpointAuthMethods.Contains("client_secret_post", StringComparer.OrdinalIgnoreCase)
                    ? "client_secret_post"
                    : meta.TokenEndpointAuthMethods[0];

        var payload = new JsonObject
        {
            ["client_name"] = ClientDisplayName,
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = preferredAuthMethod,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, meta.RegistrationEndpoint);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} from registration endpoint");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var clientId = GetOptionalString(root, "client_id");
        if (string.IsNullOrWhiteSpace(clientId)) return null;

        var clientSecret = GetOptionalString(root, "client_secret");
        var authMethod = GetOptionalString(root, "token_endpoint_auth_method") ?? preferredAuthMethod;
        return new RegisteredClient(clientId, clientSecret, authMethod);
    }

    private async Task<BotOAuthCredential?> ExchangeCodeAsync(
        BotMcpOAuthMetadata meta,
        RegisteredClient client,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("client_id", client.ClientId),
            new("code_verifier", codeVerifier),
        };
        if (!string.IsNullOrWhiteSpace(meta.Resource))
        {
            fields.Add(new("resource", meta.Resource));
        }

        using var req = CreateTokenRequest(new Uri(meta.TokenEndpoint), fields, client.ClientId, client.ClientSecret, client.TokenEndpointAuthMethod);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var errMsg = TryExtractOAuthError(body) ?? $"HTTP {(int)resp.StatusCode}";
            throw new InvalidOperationException($"Token exchange failed: {errMsg}");
        }

        return ParseTokenResponse(
            body,
            meta.TokenEndpoint,
            client.ClientId,
            client.ClientSecret,
            client.TokenEndpointAuthMethod,
            meta.Resource,
            fallbackRefreshToken: null,
            fallbackScope: meta.Scope);
    }

    private static HttpRequestMessage CreateTokenRequest(
        Uri tokenUri,
        List<KeyValuePair<string, string>> fields,
        string clientId,
        string? clientSecret,
        string? authMethod)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, tokenUri);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            if (string.Equals(authMethod, "client_secret_basic", StringComparison.OrdinalIgnoreCase))
            {
                var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}"));
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
            }
            else
            {
                fields.Add(new("client_secret", clientSecret));
            }
        }

        req.Content = new FormUrlEncodedContent(fields);
        return req;
    }

    private static BotOAuthCredential? ParseTokenResponse(
        string body,
        string tokenEndpoint,
        string clientId,
        string? clientSecret,
        string? authMethod,
        string? resource,
        string? fallbackRefreshToken,
        string? fallbackScope)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = GetOptionalString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        var refreshToken = GetOptionalString(root, "refresh_token") ?? fallbackRefreshToken;
        var scope = GetOptionalString(root, "scope") ?? fallbackScope;
        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_in", out var expEl))
        {
            if (expEl.ValueKind == JsonValueKind.Number && expEl.TryGetInt64(out var secs) && secs > 0)
            {
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(secs);
            }
            else if (expEl.ValueKind == JsonValueKind.String && long.TryParse(expEl.GetString(), out var parsedSecs) && parsedSecs > 0)
            {
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(parsedSecs);
            }
        }

        return new BotOAuthCredential(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: expiresAt,
            TokenEndpoint: tokenEndpoint,
            ClientId: clientId,
            ClientSecret: clientSecret,
            TokenEndpointAuthMethod: authMethod,
            Resource: resource,
            Scope: scope);
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string url, CancellationToken ct)
    {
        if (!BotConnectorRuntime.TryValidateHttpUrl(url, out var uri)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<CallbackOutcome> WaitForCallbackAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var stream = client.GetStream();

            var requestLine = await ReadHttpRequestLineAsync(stream, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine)) continue;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) continue;

            var target = parts[1];
            var qIdx = target.IndexOf('?');
            var path = qIdx >= 0 ? target[..qIdx] : target;
            if (!string.Equals(path, "/callback", StringComparison.Ordinal))
            {
                await WriteHtmlResponseAsync(stream, 404, "Not Found", "Not Found", ct).ConfigureAwait(false);
                continue;
            }

            var query = qIdx >= 0 ? ParseQueryString(target[(qIdx + 1)..]) : new Dictionary<string, string>(StringComparer.Ordinal);

            if (query.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            {
                query.TryGetValue("error_description", out var desc);
                var message = string.IsNullOrWhiteSpace(desc) ? error : $"{error}: {desc}";
                await WriteHtmlResponseAsync(stream, 200, "OK", BuildCallbackHtml(success: false, message), ct).ConfigureAwait(false);
                return new CallbackOutcome(null, message);
            }

            query.TryGetValue("state", out var actualState);
            if (!string.Equals(actualState, expectedState, StringComparison.Ordinal))
            {
                const string mismatch = "OAuth state mismatch.";
                await WriteHtmlResponseAsync(stream, 400, "Bad Request", BuildCallbackHtml(success: false, mismatch), ct).ConfigureAwait(false);
                return new CallbackOutcome(null, mismatch);
            }

            query.TryGetValue("code", out var code);
            await WriteHtmlResponseAsync(stream, 200, "OK", BuildCallbackHtml(success: true, null), ct).ConfigureAwait(false);
            return new CallbackOutcome(code, null);
        }
    }

    private static async Task<string?> ReadHttpRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (sb.Length < 16384)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) || sb.ToString().Contains("\n\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = sb.ToString();
        var lineEnd = text.IndexOfAny(['\r', '\n']);
        return lineEnd > 0 ? text[..lineEnd] : (text.Length > 0 ? text : null);
    }

    private static async Task WriteHtmlResponseAsync(NetworkStream stream, int statusCode, string reason, string html, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 {statusCode} {reason}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {bodyBytes.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string BuildCallbackHtml(bool success, string? error)
    {
        var title = success ? "Authorization Complete" : "Authorization Failed";
        var heading = success ? "授权成功 · Signed in" : "授权失败 · Sign-in failed";
        var detail = success
            ? "您可以关闭此标签页并返回 Octopus。You can close this tab and return to Octopus."
            : WebUtility.HtmlEncode(error ?? "Unknown OAuth error");
        var accent = success ? "#22c55e" : "#ef4444";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width,initial-scale=1" />
              <title>{{title}} — Octopus</title>
              <style>
                :root { color-scheme: light dark; }
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; background: Canvas; color: CanvasText; }
                .card { max-width: 420px; padding: 28px 32px; border-radius: 14px; border: 1px solid rgba(128,128,128,.25); text-align: center; }
                .dot { width: 12px; height: 12px; border-radius: 999px; background: {{accent}}; display: inline-block; margin-bottom: 12px; }
                h1 { font-size: 18px; margin: 0 0 10px; }
                p { font-size: 14px; opacity: .78; margin: 0; line-height: 1.5; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="dot"></div>
                <h1>{{heading}}</h1>
                <p>{{detail}}</p>
              </div>
            </body>
            </html>
            """;
    }

    private async Task<(string? ResourceMetadataUrl, string? Scope)> ProbeAuthChallengeAsync(Uri mcpUri, HttpMethod method, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, mcpUri);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (method == HttpMethod.Post)
            {
                req.Content = new StringContent(DiscoveryInitializeJson, Encoding.UTF8, "application/json");
            }
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var wwwHeader = string.Join(", ", resp.Headers.WwwAuthenticate.Select(h => h.ToString()));
                return (ExtractAuthParam(wwwHeader, "resource_metadata"), ExtractAuthParam(wwwHeader, "scope"));
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        return (null, null);
    }

    internal static IReadOnlyList<string> BuildProtectedResourceCandidates(Uri mcpUri, string? wwwResourceMetadataUrl)
    {
        var list = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(wwwResourceMetadataUrl) && BotConnectorRuntime.TryValidateHttpUrl(wwwResourceMetadataUrl, out var hinted))
        {
            list.Add(hinted.AbsoluteUri);
        }

        var origin = mcpUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var path = mcpUri.AbsolutePath.TrimEnd('/');
        if (!string.IsNullOrEmpty(path) && path != "/")
        {
            AddUnique(list, $"{origin}/.well-known/oauth-protected-resource{path}");
            AddUnique(list, $"{origin}{path}/.well-known/oauth-protected-resource");
        }
        AddUnique(list, $"{origin}/.well-known/oauth-protected-resource");
        return list;
    }

    internal static IReadOnlyList<string> BuildAuthServerMetadataCandidates(Uri issuerUri)
    {
        var list = new List<string>(5);
        var origin = issuerUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var path = issuerUri.AbsolutePath.TrimEnd('/');
        if (!string.IsNullOrEmpty(path) && path != "/")
        {
            AddUnique(list, $"{origin}/.well-known/oauth-authorization-server{path}");
            AddUnique(list, $"{origin}/.well-known/openid-configuration{path}");
            AddUnique(list, $"{origin}{path}/.well-known/openid-configuration");
        }
        AddUnique(list, $"{origin}/.well-known/oauth-authorization-server");
        AddUnique(list, $"{origin}/.well-known/openid-configuration");
        return list;
    }

    internal static string BuildAuthorizationUrl(
        BotMcpOAuthMetadata meta,
        string clientId,
        string redirectUri,
        string codeChallenge,
        string state)
    {
        var query = new List<(string Key, string Value)>
        {
            ("response_type", "code"),
            ("client_id", clientId),
            ("redirect_uri", redirectUri),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            ("state", state),
        };
        if (!string.IsNullOrWhiteSpace(meta.Scope))
        {
            query.Add(("scope", meta.Scope));
        }
        if (!string.IsNullOrWhiteSpace(meta.Resource))
        {
            query.Add(("resource", meta.Resource));
        }

        var sep = meta.AuthorizationEndpoint.Contains('?') ? '&' : '?';
        var qs = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{meta.AuthorizationEndpoint}{sep}{qs}";
    }

    internal static string? ExtractAuthParam(string headerValue, string paramName)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        var match = AuthParamRegex().Match(headerValue);
        while (match.Success)
        {
            if (string.Equals(match.Groups["k"].Value, paramName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["v"].Value;
            }
            match = match.NextMatch();
        }
        return null;
    }

    internal static Dictionary<string, string> ParseQueryString(string rawQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(rawQuery)) return result;
        foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
            {
                result[Uri.UnescapeDataString(pair.Replace('+', ' '))] = "";
            }
            else
            {
                var k = Uri.UnescapeDataString(pair[..idx].Replace('+', ' '));
                var v = Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
                result[k] = v;
            }
        }
        return result;
    }

    internal static string GenerateBase64UrlToken(int byteCount) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    internal static string ComputeS256Challenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void AddUnique(List<string> list, string url)
    {
        if (!list.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(url);
        }
    }

    private static string? GetOptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? TryExtractOAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var err = GetOptionalString(root, "error");
            var desc = GetOptionalString(root, "error_description");
            if (!string.IsNullOrWhiteSpace(err) && !string.IsNullOrWhiteSpace(desc)) return $"{err}: {desc}";
            return desc ?? err;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<k>[A-Za-z0-9_\-]+)\s*=\s*(?:""(?<q>[^""]*)""|(?<v>[^\s,]+))", RegexOptions.CultureInvariant)]
    private static partial Regex AuthParamRegex();

    private sealed record RegisteredClient(string ClientId, string? ClientSecret, string TokenEndpointAuthMethod);
    private sealed record CallbackOutcome(string? Code, string? Error);
}
