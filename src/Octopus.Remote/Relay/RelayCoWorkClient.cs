using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Octopus.Remote.Relay;

/// <summary>Live CoWork wallet overview returned by Octopus Server (<c>/v1/integrations/cowork</c>).</summary>
public sealed record CoWorkWalletInfo(
    string Currency,
    decimal AvailableBalance,
    decimal CashBalance,
    decimal GiftBalance,
    decimal FrozenAmount,
    decimal TotalConsumed);

/// <summary>Current 1:1 CoWork binding status for the signed-in Octopus account.</summary>
public sealed record CoWorkIntegrationStatus(
    bool Enabled,
    bool Bound,
    string? Status,
    string? CoWorkUserId,
    string? CoWorkEmail,
    string? CoWorkDisplayName,
    string? SelectedTeamId,
    string? SelectedTeamName,
    string? TeamRole,
    CoWorkWalletInfo? Wallet,
    string GatewayBaseUrl,
    string? BindUrl,
    DateTimeOffset? BoundAt,
    string? Error);

/// <summary>One selectable billing Team in the bound CoWork account.</summary>
public sealed record CoWorkTeamInfo(
    string Id,
    string Name,
    string Type,
    string Role,
    int MemberCount,
    bool IsSelected);

/// <summary>Available CoWork Teams for the bound account.</summary>
public sealed record CoWorkTeamsInfo(IReadOnlyList<CoWorkTeamInfo> Teams, string? SelectedTeamId);

/// <summary>Short-lived, narrow-scope CoWork delegated JWT (<c>models:read inference:write</c>) for local inference.</summary>
public sealed record CoWorkDelegatedToken(
    string AccessToken,
    int ExpiresIn,
    DateTimeOffset ExpiresAt,
    string Scope,
    string GatewayBaseUrl,
    string? SelectedTeamId,
    string? SelectedTeamName)
{
    public override string ToString() => $"CoWork delegated token ({SelectedTeamName ?? "default"}, expires {ExpiresAt:O})";
}

/// <summary>Result of a delegated token request, distinguishing auth/binding errors from network failures.</summary>
public sealed record CoWorkDelegateResult(bool Succeeded, CoWorkDelegatedToken? Token, string? Error);

internal sealed class CoWorkWalletDto
{
    public string? Currency { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal CashBalance { get; set; }
    public decimal GiftBalance { get; set; }
    public decimal FrozenAmount { get; set; }
    public decimal TotalConsumed { get; set; }
}

internal sealed class CoWorkStatusDto
{
    public bool Enabled { get; set; }
    public bool Bound { get; set; }
    public string? Status { get; set; }
    public string? CoWorkUserId { get; set; }
    public string? CoWorkEmail { get; set; }
    public string? CoWorkDisplayName { get; set; }
    public string? SelectedTeamId { get; set; }
    public string? SelectedTeamName { get; set; }
    public string? TeamRole { get; set; }
    public CoWorkWalletDto? Wallet { get; set; }
    public string? GatewayBaseUrl { get; set; }
    public string? BindUrl { get; set; }
    public DateTimeOffset? BoundAt { get; set; }
    public string? Error { get; set; }
}

internal sealed class CoWorkTeamOptionDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Role { get; set; }
    public int MemberCount { get; set; }
    public bool IsSelected { get; set; }
}

internal sealed class CoWorkTeamsDto
{
    public List<CoWorkTeamOptionDto>? Teams { get; set; }
    public string? SelectedTeamId { get; set; }
}

internal sealed class CoWorkSelectTeamRequestDto
{
    public string? TeamId { get; set; }
}

internal sealed class CoWorkSelectTeamResponseDto
{
    public string? SelectedTeamId { get; set; }
    public string? SelectedTeamName { get; set; }
}

internal sealed class CoWorkDelegateRequestDto
{
    public string? Scope { get; set; }
}

internal sealed class CoWorkDelegateResponseDto
{
    public string? AccessToken { get; set; }
    public int ExpiresIn { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Scope { get; set; }
    public string? GatewayBaseUrl { get; set; }
    public string? SelectedTeamId { get; set; }
    public string? SelectedTeamName { get; set; }
    public string? Error { get; set; }
}

internal sealed class CoWorkErrorDto
{
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CoWorkStatusDto))]
[JsonSerializable(typeof(CoWorkTeamsDto))]
[JsonSerializable(typeof(CoWorkSelectTeamRequestDto))]
[JsonSerializable(typeof(CoWorkSelectTeamResponseDto))]
[JsonSerializable(typeof(CoWorkDelegateRequestDto))]
[JsonSerializable(typeof(CoWorkDelegateResponseDto))]
[JsonSerializable(typeof(CoWorkErrorDto))]
internal sealed partial class RelayCoWorkJsonContext : JsonSerializerContext;

/// <summary>
/// AOT-compatible client for Octopus Server's <c>/v1/integrations/cowork/*</c> endpoints, authenticated with the
/// device's Octopus Bearer access token.
/// </summary>
public static class RelayCoWorkClient
{
    private static readonly Lazy<HttpClient> HttpClientLazy = new(static () => new HttpClient(RelayHttp.CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(15),
    });

    private static HttpClient Http => HttpClientLazy.Value;

    public static async Task<CoWorkIntegrationStatus?> GetStatusAsync(
        Uri relay,
        string accessToken,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var (status, bytes) = await SendAsync(httpClient ?? Http, HttpMethod.Get, new Uri(relay, "v1/integrations/cowork"), accessToken, null, cancellationToken).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || bytes.Length == 0)
        {
            return null;
        }

        var dto = Deserialize(bytes, RelayCoWorkJsonContext.Default.CoWorkStatusDto);
        if (dto is null)
        {
            return null;
        }

        var wallet = dto.Wallet is { } w
            ? new CoWorkWalletInfo(w.Currency ?? "USD", w.AvailableBalance, w.CashBalance, w.GiftBalance, w.FrozenAmount, w.TotalConsumed)
            : null;
        return new CoWorkIntegrationStatus(
            dto.Enabled,
            dto.Bound,
            dto.Status,
            dto.CoWorkUserId,
            dto.CoWorkEmail,
            dto.CoWorkDisplayName,
            dto.SelectedTeamId,
            dto.SelectedTeamName,
            dto.TeamRole,
            wallet,
            dto.GatewayBaseUrl ?? "https://nextco.work",
            dto.BindUrl,
            dto.BoundAt,
            dto.Error);
    }

    public static async Task<CoWorkTeamsInfo?> GetTeamsAsync(
        Uri relay,
        string accessToken,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var (status, bytes) = await SendAsync(httpClient ?? Http, HttpMethod.Get, new Uri(relay, "v1/integrations/cowork/teams"), accessToken, null, cancellationToken).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || bytes.Length == 0)
        {
            return null;
        }

        var dto = Deserialize(bytes, RelayCoWorkJsonContext.Default.CoWorkTeamsDto);
        if (dto is null)
        {
            return null;
        }

        var items = (dto.Teams ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => new CoWorkTeamInfo(t.Id!, t.Name!, t.Type ?? "Team", t.Role ?? "Member", t.MemberCount, t.IsSelected))
            .ToList();
        return new CoWorkTeamsInfo(items, dto.SelectedTeamId);
    }

    public static async Task<(bool Succeeded, string? SelectedTeamId, string? SelectedTeamName, string? Error)> SelectTeamAsync(
        Uri relay,
        string accessToken,
        string teamId,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CoWorkSelectTeamRequestDto { TeamId = teamId }, RelayCoWorkJsonContext.Default.CoWorkSelectTeamRequestDto);
        var (status, bytes) = await SendAsync(httpClient ?? Http, HttpMethod.Post, new Uri(relay, "v1/integrations/cowork/team"), accessToken, payload, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.OK)
        {
            var dto = Deserialize(bytes, RelayCoWorkJsonContext.Default.CoWorkSelectTeamResponseDto);
            return (true, dto?.SelectedTeamId ?? teamId, dto?.SelectedTeamName, null);
        }

        return (false, null, null, ReadError(bytes) ?? $"http_{(int)status}");
    }

    public static async Task<CoWorkDelegateResult> MintDelegatedTokenAsync(
        Uri relay,
        string accessToken,
        string? scope = null,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CoWorkDelegateRequestDto { Scope = scope }, RelayCoWorkJsonContext.Default.CoWorkDelegateRequestDto);
        var (status, bytes) = await SendAsync(httpClient ?? Http, HttpMethod.Post, new Uri(relay, "v1/integrations/cowork/delegate"), accessToken, payload, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.OK)
        {
            var dto = Deserialize(bytes, RelayCoWorkJsonContext.Default.CoWorkDelegateResponseDto);
            if (dto is { AccessToken.Length: > 0 })
            {
                var token = new CoWorkDelegatedToken(
                    dto.AccessToken,
                    Math.Max(1, dto.ExpiresIn),
                    dto.ExpiresAt == default ? DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, dto.ExpiresIn)) : dto.ExpiresAt,
                    dto.Scope ?? "models:read inference:write",
                    dto.GatewayBaseUrl ?? "https://nextco.work",
                    dto.SelectedTeamId,
                    dto.SelectedTeamName);
                return new CoWorkDelegateResult(true, token, null);
            }

            return new CoWorkDelegateResult(false, null, "invalid_response");
        }

        return new CoWorkDelegateResult(false, null, ReadError(bytes) ?? $"http_{(int)status}");
    }

    public static async Task<bool> UnbindAsync(
        Uri relay,
        string accessToken,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var (status, _) = await SendAsync(httpClient ?? Http, HttpMethod.Delete, new Uri(relay, "v1/integrations/cowork"), accessToken, "{}"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        return status is HttpStatusCode.NoContent or HttpStatusCode.OK;
    }

    private static async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(
        HttpClient client,
        HttpMethod method,
        Uri url,
        string accessToken,
        byte[]? jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (jsonBody is not null)
        {
            request.Content = new ByteArrayContent(jsonBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (HttpStatusCode.ServiceUnavailable, Encoding.UTF8.GetBytes("{\"error\":\"network_error\"}"));
        }
    }

    private static T? Deserialize<T>(byte[] bytes, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (bytes.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize(bytes, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadError(byte[] bytes) =>
        Deserialize(bytes, RelayCoWorkJsonContext.Default.CoWorkErrorDto)?.Error;
}
