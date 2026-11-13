global using Xunit;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Octopus.Core.Updates;
using Octopus.Platform;
using Octopus.Platform.Updates;

namespace Octopus.Updates.Tests;

/// <summary>A signing key made for the test run, in the formats the release tooling produces.</summary>
public sealed class TestKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    /// <summary>What <c>openssl dgst -sha256 -sign key | base64</c> writes.</summary>
    public byte[] Sign(byte[] data) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));

    public void Dispose() => _key.Dispose();
}

/// <summary>Serves fixed answers by URL; honours Range requests for the files it holds.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];
    public List<HttpRequestMessage> Requests { get; } = [];
    /// <summary>When set, a body is cut off after this many bytes (the connection "drops").</summary>
    public int? FailAfterBytes { get; set; }
    public bool IgnoreRange { get; set; }
    /// <summary>When set, packages (.zip) wait for it before giving any byte (a download that is "in progress").</summary>
    public TaskCompletionSource? Hold { get; set; }

    public void Json(string url, string json, string? etag = null) => _routes[url] = request =>
    {
        if (etag is not null && request.Headers.TryGetValues("If-None-Match", out var match) && match.Contains(etag))
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    };

    public void File(string url, byte[] content) => _routes[url] = request =>
    {
        var from = 0L;
        if (!IgnoreRange && request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } start })
        {
            if (start >= content.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            from = start;
        }
        var body = content.AsSpan((int)from).ToArray();
        Stream stream = FailAfterBytes is { } cut && cut < body.Length ? new DroppingStream(body, cut)
            : Hold is { } hold && url.EndsWith(".zip", StringComparison.Ordinal) ? new HeldStream(body, hold.Task)
            : new MemoryStream(body);
        var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = body.Length;
        return response;
    };

    public void Status(string url, HttpStatusCode status, Action<HttpResponseMessage>? configure = null) => _routes[url] = _ =>
    {
        var response = new HttpResponseMessage(status);
        configure?.Invoke(response);
        return response;
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var url = request.RequestUri!.ToString();
        return Task.FromResult(_routes.TryGetValue(url, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Gives nothing until the gate opens.</summary>
    private sealed class HeldStream(byte[] data, Task gate) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>Gives <c>cut</c> bytes, then fails like a dropped connection.</summary>
    private sealed class DroppingStream(byte[] data, int cut) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= cut) throw new IOException("connection reset");
            var max = (int)Math.Min(buffer.Length, cut - Position);
            return await base.ReadAsync(buffer[..max], cancellationToken);
        }
    }
}

/// <summary>Builds releases the way CI publishes them: packages, a signed manifest, GitHub's release list.</summary>
public sealed class FakeReleases(FakeHttp http, TestKey key)
{
    public const string ListUrl = "https://api.github.test/repos/o/r/releases";
    private readonly List<string> _releases = [];

    /// <param name="signedBy">Another key, to publish a forged manifest.</param>
    public byte[] Add(string version, string target = "osx-arm64", byte[]? package = null, bool draft = false, TestKey? signedBy = null, bool withManifest = true, string? manifestVersion = null, string? notes = null)
    {
        package ??= Encoding.UTF8.GetBytes($"package {version} {Guid.NewGuid()}");
        var name = $"Octopus-{version}-{target}.zip";
        var packageUrl = $"https://dl.test/{version}/{name}";
        http.File(packageUrl, package);
        var manifest = new UpdateManifest
        {
            Version = manifestVersion ?? version,
            PublishedAt = DateTimeOffset.Parse("2026-11-12T08:00:00Z"),
            NotesUrl = $"https://github.test/o/r/releases/tag/v{version}",
            Assets =
            {
                [target] = new UpdateAsset { Name = name, Url = packageUrl, Size = package.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(package)) },
            },
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdatesJsonContext.Default.UpdateManifest);
        http.File($"https://dl.test/{version}/update.json", json);
        http.File($"https://dl.test/{version}/update.json.sig", (signedBy ?? key).Sign(json));
        var assets = withManifest
            ? $$"""
              [{"name":"update.json","browser_download_url":"https://dl.test/{{version}}/update.json","size":{{json.Length}}},
               {"name":"update.json.sig","browser_download_url":"https://dl.test/{{version}}/update.json.sig","size":100},
               {"name":"{{name}}","browser_download_url":"{{packageUrl}}","size":{{package.Length}}}]
              """
            : "[]";
        _releases.Insert(0, $$"""{"tag_name":"v{{version}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(version.Contains('-') ? "true" : "false")}},"html_url":"https://github.test/o/r/releases/tag/v{{version}}","body":{{(notes is null ? "null" : JsonSerializer.Serialize(notes))}},"assets":{{assets}}}""");
        http.Json(ListUrl, "[" + string.Join(",", _releases) + "]");
        return package;
    }
}

public sealed class TempPaths : IAppPaths, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("octopus-updates-tests-").FullName;
    public string DataDirectory => Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
    public string CacheDirectory => Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;
    public string Root => _root;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}

/// <summary>Records what the service asks of the platform.</summary>
public sealed class FakeInstaller(InstallKind kind = InstallKind.Installed) : IUpdateInstaller
{
    public string Target => "osx-arm64";
    public string Location => "/Applications/Octopus.app";
    public InstallKind Kind { get; set; } = kind;
    public Exception? PrepareFails { get; set; }
    public List<(string Prepared, InstallRequest Request)> Launched { get; } = [];

    public InstallKind Detect() => Kind;

    public Task<string> PrepareAsync(string package, string workDirectory, CancellationToken cancellationToken)
    {
        if (PrepareFails is not null) throw PrepareFails;
        return Task.FromResult(package);
    }

    public void LaunchInstall(string prepared, InstallRequest request) => Launched.Add((prepared, request));
}

public static class Wait
{
    /// <summary>Waits (up to 5 s) for the service to reach a state.</summary>
    public static async Task ForAsync(IUpdateService service, Func<UpdateSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition(service.Current))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Still {service.Current.Status} ({service.Current.Error}: {service.Current.ErrorDetail}).");
            await Task.Delay(10);
        }
    }
}

public static class Versions
{
    public static AppVersion V(string text) => AppVersion.Parse(text);
}
