using System.Net;
using System.Security.Cryptography;
using System.Text;
using Octopus.Plugins.Marketplace;
using Xunit;

namespace Octopus.Plugins.Tests;

public sealed class MarketplaceSecurityPolicyTests
{
    private static MarketplacePluginVersion MakeVersion(string sha256, string downloadUrl) => new(
        PluginId: "acme.csv",
        Version: "1.0.0",
        ApiVersion: 0,
        OctopusRange: ">=0.1.0",
        Permissions: ["fs.project"],
        RiskLevel: "medium",
        PackageSize: 21,
        Sha256: sha256,
        DownloadUrl: downloadUrl,
        ReviewStatus: "approved",
        Changelog: null,
        SubmissionNotes: null,
        SubmittedAt: DateTime.UnixEpoch,
        PublishedAt: DateTime.UnixEpoch);

    private static string CatalogJson(string downloadUrl, string sha256) => $$"""
        {
          "total": 1,
          "page": 1,
          "pageSize": 24,
          "items": [
            {
              "plugin": {
                "id": "acme.csv",
                "publisherSlug": "acme",
                "publisherName": "Acme",
                "publisherVerified": true,
                "name": "CSV Table",
                "description": "CSV viewer",
                "category": "editor",
                "tags": ["csv"],
                "downloads": 1,
                "featured": false,
                "latestVersion": "1.0.0",
                "updatedAt": "2026-11-25T00:00:00Z"
              },
              "latest": {
                "pluginId": "acme.csv",
                "version": "1.0.0",
                "apiVersion": 0,
                "permissions": ["fs.project"],
                "riskLevel": "medium",
                "packageSize": 21,
                "sha256": "{{sha256}}",
                "downloadUrl": "{{downloadUrl}}",
                "reviewStatus": "approved",
                "submittedAt": "2026-11-25T00:00:00Z",
                "publishedAt": "2026-11-25T00:00:00Z"
              }
            }
          ]
        }
        """;

    private static string DetailJson(string downloadUrl, string sha256) => $$"""
        {
          "plugin": {
            "id": "acme.csv",
            "publisherSlug": "acme",
            "publisherName": "Acme",
            "publisherVerified": true,
            "name": "CSV Table",
            "description": "CSV viewer",
            "category": "editor",
            "tags": ["csv"],
            "downloads": 1,
            "featured": false,
            "latestVersion": "1.0.0",
            "updatedAt": "2026-11-25T00:00:00Z"
          },
          "readmeMd": "# CSV Table",
          "versions": [
            {
              "pluginId": "acme.csv",
              "version": "1.0.0",
              "apiVersion": 0,
              "permissions": ["fs.project"],
              "riskLevel": "medium",
              "packageSize": 21,
              "sha256": "{{sha256}}",
              "downloadUrl": "{{downloadUrl}}",
              "reviewStatus": "approved",
              "submittedAt": "2026-11-25T00:00:00Z",
              "publishedAt": "2026-11-25T00:00:00Z"
            }
          ]
        }
        """;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task DownloadVerifiedAsync_rejects_missing_or_malformed_sha256_before_downloading(string sha256)
    {
        var requests = 0;
        var payload = Encoding.UTF8.GetBytes("fake-octoplugin-bytes");
        var handler = new CountingHttpHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });

        var client = new PluginMarketplaceClient(new HttpClient(handler));
        var version = MakeVersion(sha256, "https://example.test/plugins/download/acme.csv/1.0.0.octoplugin");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DownloadVerifiedAsync("https://example.test", version, TestContext.Current.CancellationToken));
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("http://marketplace.example.test", false)]
    [InlineData("http://octoterm.si", false)]
    [InlineData("http://192.168.1.25:5000", false)]
    [InlineData("http://0.0.0.0:5000", false)]
    [InlineData("http://localhost.example.test:5000", false)]
    [InlineData("https://user:pass@octoterm.si", false)]
    [InlineData("ftp://octoterm.si", false)]
    [InlineData("https://octoterm.si", true)]
    [InlineData("https://marketplace.example.test:8443", true)]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://[::1]:5000", true)]
    public void Deep_link_enforces_https_externally_and_allows_localhost_development(string serverUrl, bool expectedValid)
    {
        var link = PluginInstallLink.BuildUri("acme.csv", "1.0.0", serverUrl);
        var ok = PluginInstallLink.TryParse(new Uri(link), out var request, out var error);

        Assert.Equal(expectedValid, ok);
        if (expectedValid)
        {
            Assert.Null(error);
            Assert.NotNull(request);
            Assert.Equal(serverUrl, request!.ServerUrl);
        }
        else
        {
            Assert.Null(request);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Theory]
    [InlineData("http://marketplace.example.test")]
    [InlineData("http://192.168.1.25:5000")]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://localhost.example.test:5000")]
    [InlineData("https://user:pass@marketplace.example.test")]
    public async Task Marketplace_client_rejects_external_http_endpoints_without_sending_requests(string serverUrl)
    {
        var payload = Encoding.UTF8.GetBytes("fake-octoplugin-bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var requests = 0;
        var handler = new CountingHttpHandler(req =>
        {
            requests++;
            if (req.RequestUri!.AbsolutePath == "/v1/plugins/catalog")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CatalogJson("", sha), Encoding.UTF8, "application/json"),
                };
            }
            if (req.RequestUri.AbsolutePath == "/api/plugins/acme.csv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(DetailJson("", sha), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });

        var ct = TestContext.Current.CancellationToken;
        var client = new PluginMarketplaceClient(new HttpClient(handler));
        var version = MakeVersion(sha, "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(serverUrl, cancellationToken: ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDetailAsync(serverUrl, "acme.csv", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadVerifiedAsync(serverUrl, version, ct));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Marketplace_client_enforces_https_on_download_urls_per_source_and_supports_localhost_dev()
    {
        var payload = Encoding.UTF8.GetBytes("fake-octoplugin-bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var requests = 0;
        var handler = new CountingHttpHandler(req =>
        {
            requests++;
            if (req.RequestUri!.AbsolutePath == "/v1/plugins/catalog")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CatalogJson("http://localhost:5000/plugins/download/acme.csv/1.0.0.octoplugin", sha), Encoding.UTF8, "application/json"),
                };
            }
            if (req.RequestUri.AbsolutePath == "/api/plugins/acme.csv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(DetailJson("http://localhost:5000/plugins/download/acme.csv/1.0.0.octoplugin", sha), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });

        var ct = TestContext.Current.CancellationToken;
        var client = new PluginMarketplaceClient(new HttpClient(handler));

        // External HTTPS marketplace cannot downgrade downloads to http:// (neither remote nor localhost).
        var externalHttpDownload = MakeVersion(sha, "http://cdn.example.test/acme.csv.octoplugin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadVerifiedAsync("https://example.test", externalHttpDownload, ct));

        var loopbackDowngradeFromRemote = MakeVersion(sha, "http://127.0.0.1:5000/acme.csv.octoplugin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadVerifiedAsync("https://example.test", loopbackDowngradeFromRemote, ct));
        Assert.Equal(0, requests);

        // Localhost development server deliberately supports http:// for catalog, detail, and localhost downloads,
        // while still rejecting non-localhost http:// download URLs.
        var localCatalog = await client.SearchAsync("http://localhost:5000", cancellationToken: ct);
        Assert.Single(localCatalog.Items);

        var localDetail = await client.GetDetailAsync("http://127.0.0.1:5000", "acme.csv", ct);
        Assert.NotNull(localDetail);

        var localDownload = await client.DownloadVerifiedAsync("http://localhost:5000", localCatalog.Items[0].Latest, ct);
        try
        {
            Assert.Equal(payload, File.ReadAllBytes(localDownload));
        }
        finally
        {
            if (File.Exists(localDownload)) File.Delete(localDownload);
        }

        var requestsBeforeBadExternal = requests;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadVerifiedAsync("http://localhost:5000", externalHttpDownload, ct));
        Assert.Equal(requestsBeforeBadExternal, requests);
    }

    [Theory]
    [InlineData("https://example.test", "/plugins/download/acme.csv/1.0.0.octoplugin", "https://example.test/plugins/download/acme.csv/1.0.0.octoplugin")]
    [InlineData("https://example.test/market", "/plugins/download/acme.csv/1.0.0.octoplugin", "https://example.test/plugins/download/acme.csv/1.0.0.octoplugin")]
    [InlineData("https://example.test", "packages/acme.csv/1.0.0.octoplugin", "https://example.test/packages/acme.csv/1.0.0.octoplugin")]
    [InlineData("https://example.test/market", "packages/acme.csv/1.0.0.octoplugin", "https://example.test/market/packages/acme.csv/1.0.0.octoplugin")]
    [InlineData("http://localhost:5000", "/plugins/download/acme.csv/1.0.0.octoplugin", "http://localhost:5000/plugins/download/acme.csv/1.0.0.octoplugin")]
    [InlineData("http://localhost:5000", "packages/acme.csv/1.0.0.octoplugin", "http://localhost:5000/packages/acme.csv/1.0.0.octoplugin")]
    [InlineData("https://example.test", "", "https://example.test/plugins/download/acme.csv/1.0.0.octoplugin")]
    public void ResolveDownloadUrl_preserves_root_relative_and_relative_package_paths(
        string serverBase,
        string rawDownloadUrl,
        string expectedUrl)
    {
        var baseUri = MarketplaceEndpointPolicy.ValidateServerUrl(serverBase);
        var resolved = MarketplaceEndpointPolicy.ResolveDownloadUrl(baseUri, rawDownloadUrl, "acme.csv", "1.0.0");

        Assert.Equal(expectedUrl, resolved.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://example.test", "//cdn.example.test/acme.csv.octoplugin")]
    [InlineData("https://example.test", "/\\cdn.example.test/acme.csv.octoplugin")]
    [InlineData("https://example.test", "file:///etc/passwd")]
    [InlineData("https://example.test", "http://cdn.example.test/acme.csv.octoplugin")]
    [InlineData("https://example.test", "http://127.0.0.1:5000/acme.csv.octoplugin")]
    [InlineData("https://example.test", "https://user:pass@cdn.example.test/acme.csv.octoplugin")]
    [InlineData("http://localhost:5000", "//external.example.test/acme.csv.octoplugin")]
    public void ResolveDownloadUrl_rejects_unsafe_or_protocol_relative_download_paths(string serverBase, string rawDownloadUrl)
    {
        var baseUri = MarketplaceEndpointPolicy.ValidateServerUrl(serverBase);
        var ok = MarketplaceEndpointPolicy.TryResolveDownloadUrl(baseUri, rawDownloadUrl, "acme.csv", "1.0.0", out var resolved, out var error);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task Marketplace_client_preflights_redirects_before_fetching_disallowed_destinations_on_all_paths()
    {
        var payload = Encoding.UTF8.GetBytes("redirected-octoplugin-bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var requested = new List<string>();
        var handler = new CountingHttpHandler(req =>
        {
            requested.Add(req.RequestUri!.AbsoluteUri);
            if (req.RequestUri.Host == "localhost" && req.RequestUri.AbsolutePath == "/v1/plugins/catalog")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("http://external.example.test/v1/plugins/catalog");
                return r;
            }
            if (req.RequestUri.Host == "localhost" && req.RequestUri.AbsolutePath == "/api/plugins/acme.csv")
            {
                var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                r.Headers.Location = new Uri("http://external.example.test/api/plugins/acme.csv");
                return r;
            }
            if (req.RequestUri.Host == "localhost" && req.RequestUri.AbsolutePath == "/plugins/download/acme.csv/1.0.0.octoplugin")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("http://external.example.test/acme.csv.octoplugin");
                return r;
            }
            if (req.RequestUri.Host == "example.test" && req.RequestUri.AbsolutePath == "/plugins/download/acme.csv/1.0.0.octoplugin")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("http://127.0.0.1:5000/acme.csv.octoplugin");
                return r;
            }
            if (req.RequestUri.Host == "localhost" && req.RequestUri.AbsolutePath == "/plugins/download/bounce/1.0.0.octoplugin")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://cdn.example.test/hop1");
                return r;
            }
            if (req.RequestUri.Host == "cdn.example.test" && req.RequestUri.AbsolutePath == "/hop1")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("http://127.0.0.1:5000/hop2");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });

        var ct = TestContext.Current.CancellationToken;
        var client = new PluginMarketplaceClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync("http://localhost:5000", cancellationToken: ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDetailAsync("http://localhost:5000", "acme.csv", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadVerifiedAsync("http://localhost:5000", MakeVersion(sha, ""), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadVerifiedAsync("https://example.test", MakeVersion(sha, ""), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DownloadVerifiedAsync("http://localhost:5000", MakeVersion(sha, "/plugins/download/bounce/1.0.0.octoplugin"), ct));

        Assert.DoesNotContain(requested, u => u.StartsWith("http://external.example.test", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requested, u => u.StartsWith("http://127.0.0.1:5000", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Marketplace_client_follows_validated_redirects_and_caps_redirect_loops()
    {
        var payload = Encoding.UTF8.GetBytes("redirected-valid-bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var handler = new CountingHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/v1/plugins/catalog")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("/v2/plugins/catalog", UriKind.Relative);
                return r;
            }
            if (req.RequestUri.AbsolutePath == "/v2/plugins/catalog")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CatalogJson("/plugins/download/acme.csv/1.0.0.octoplugin", sha), Encoding.UTF8, "application/json"),
                };
            }
            if (req.RequestUri.AbsolutePath == "/api/plugins/acme.csv")
            {
                var r = new HttpResponseMessage(HttpStatusCode.PermanentRedirect);
                r.Headers.Location = new Uri("http://127.0.0.1:5000/api/v2/plugins/acme.csv");
                return r;
            }
            if (req.RequestUri.AbsolutePath == "/api/v2/plugins/acme.csv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(DetailJson("/plugins/download/acme.csv/1.0.0.octoplugin", sha), Encoding.UTF8, "application/json"),
                };
            }
            if (req.RequestUri.AbsolutePath == "/plugins/download/acme.csv/1.0.0.octoplugin")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://cdn.example.test/releases/acme.csv-1.0.0.octoplugin");
                return r;
            }
            if (req.RequestUri.Host == "cdn.example.test" && req.RequestUri.AbsolutePath == "/releases/acme.csv-1.0.0.octoplugin")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            }
            if (req.RequestUri.AbsolutePath == "/api/plugins/loop")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://example.test/api/plugins/loop");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var ct = TestContext.Current.CancellationToken;
        var client = new PluginMarketplaceClient(new HttpClient(handler));

        var catalog = await client.SearchAsync("https://example.test", cancellationToken: ct);
        Assert.Single(catalog.Items);

        var detail = await client.GetDetailAsync("http://localhost:5000", "acme.csv", ct);
        Assert.NotNull(detail);

        var file = await client.DownloadVerifiedAsync("https://example.test", catalog.Items[0].Latest, ct);
        try
        {
            Assert.Equal(payload, File.ReadAllBytes(file));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDetailAsync("https://example.test", "loop", ct));
    }

    [Fact]
    public void Default_marketplace_handler_disables_automatic_redirects()
    {
        using var handler = PluginMarketplaceClient.CreateDefaultHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    private sealed class CountingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
