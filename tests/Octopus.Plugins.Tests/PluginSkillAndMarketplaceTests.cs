using System.Net;
using System.Security.Cryptography;
using System.Text;
using Octopus.Plugins.Marketplace;
using Octopus.Plugins.Skills;
using Xunit;

namespace Octopus.Plugins.Tests;

public sealed class PluginSkillAndMarketplaceTests
{
    [Fact]
    public void Default_skill_targets_match_agents_claude_cursor_codex()
    {
        var defaults = PluginSkillInstaller.BuiltInTargets
            .Where(t => t.DefaultSelected)
            .Select(t => t.Id)
            .ToArray();

        Assert.Equal(["agents", "claude", "cursor", "codex"], defaults);
    }

    [Fact]
    public async Task Skill_installer_force_overwrites_existing_targets_and_removes_stale_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"octopus-skill-test-{Guid.NewGuid():N}");
        var builtinDir = Path.Combine(root, "builtin", "octopus-plugin");
        var fakeHome = Path.Combine(root, "home");
        Directory.CreateDirectory(builtinDir);
        Directory.CreateDirectory(fakeHome);

        try
        {
            File.WriteAllText(Path.Combine(builtinDir, "SKILL.md"), "# Octopus Plugin v2");
            File.WriteAllText(Path.Combine(builtinDir, ".skill-meta.json"), """{"name":"octopus-plugin","version":"2.0.0"}""");

            // Seed stale old version in ~/.agents/skills/octopus-plugin and ~/.claude/skills/octopus-plugin
            var existingAgents = Path.Combine(fakeHome, ".agents", "skills", "octopus-plugin");
            Directory.CreateDirectory(existingAgents);
            File.WriteAllText(Path.Combine(existingAgents, "SKILL.md"), "# OLD v1");
            File.WriteAllText(Path.Combine(existingAgents, ".skill-meta.json"), """{"name":"octopus-plugin","version":"1.0.0"}""");
            File.WriteAllText(Path.Combine(existingAgents, "stale-old-api.md"), "should be deleted on overwrite");

            var installer = new PluginSkillInstaller(builtinDir, userHomeDirectory: fakeHome);
            var batch = await installer.InstallOverwriteAsync(
                ["agents", "claude", "cursor", "codex"],
                SkillInstallScope.UserGlobal,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("2.0.0", batch.SkillVersion);
            Assert.Equal("builtin", batch.Source);
            Assert.Equal(4, batch.SucceededCount);
            Assert.Equal(0, batch.FailedCount);

            // Verify agents dir was completely overwritten and stale file removed
            Assert.Equal("1.0.0", batch.Results.Single(r => r.TargetId == "agents").PreviousVersion);
            Assert.False(File.Exists(Path.Combine(existingAgents, "stale-old-api.md")));
            Assert.Equal("# Octopus Plugin v2", File.ReadAllText(Path.Combine(existingAgents, "SKILL.md")));

            // Verify all 4 default targets received the fresh Skill
            foreach (var folder in new[] { ".agents", ".claude", ".cursor", ".codex" })
            {
                var installedDir = Path.Combine(fakeHome, folder, "skills", "octopus-plugin");
                Assert.True(File.Exists(Path.Combine(installedDir, "SKILL.md")));
                Assert.Equal("2.0.0", PluginSkillInstaller.ReadInstalledVersion(installedDir));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Marketplace_client_parses_catalog_and_verifies_download_sha256()
    {
        var payload = Encoding.UTF8.GetBytes("fake-octoplugin-bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));

        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/v1/plugins/catalog")
            {
                var json = $$"""
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
                        "localizedNameZh": "CSV 表格",
                        "description": "CSV viewer",
                        "localizedDescriptionZh": "CSV 查看器",
                        "category": "editor",
                        "tags": ["csv"],
                        "downloads": 42,
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
                        "packageSize": {{payload.Length}},
                        "sha256": "{{sha}}",
                        "downloadUrl": "https://example.test/plugins/download/acme.csv/1.0.0.octoplugin",
                        "reviewStatus": "approved",
                        "submittedAt": "2026-11-25T00:00:00Z",
                        "publishedAt": "2026-11-25T00:00:00Z"
                      }
                    }
                  ]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            if (req.RequestUri.AbsolutePath == "/plugins/download/acme.csv/1.0.0.octoplugin")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var ct = TestContext.Current.CancellationToken;
        var client = new PluginMarketplaceClient(new HttpClient(handler));
        var catalog = await client.SearchAsync("https://example.test", query: "csv", apiVersion: 0, cancellationToken: ct);
        Assert.Equal(1, catalog.Total);
        Assert.Single(catalog.Items);
        Assert.Equal("acme.csv", catalog.Items[0].Plugin.Id);

        var file = await client.DownloadVerifiedAsync("https://example.test", catalog.Items[0].Latest, ct);
        try
        {
            Assert.Equal(payload, File.ReadAllBytes(file));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }

        // Bad checksum must be rejected
        var badVersion = catalog.Items[0].Latest with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadVerifiedAsync("https://example.test", badVersion, ct));
    }

    [Fact]
    public async Task Marketplace_client_fetches_plugin_detail_and_handles_not_found()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/plugins/acme.csv")
            {
                const string json = """
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
                    "downloads": 42,
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
                      "packageSize": 123,
                      "sha256": "abc",
                      "downloadUrl": "https://example.test/plugins/download/acme.csv/1.0.0.octoplugin",
                      "reviewStatus": "approved",
                      "submittedAt": "2026-11-25T00:00:00Z",
                      "publishedAt": "2026-11-25T00:00:00Z"
                    }
                  ]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var ct = TestContext.Current.CancellationToken;
        var client = new PluginMarketplaceClient(new HttpClient(handler));
        var detail = await client.GetDetailAsync("https://example.test", "acme.csv", ct);
        Assert.NotNull(detail);
        Assert.Equal("acme.csv", detail!.Plugin.Id);
        Assert.Single(detail.Versions);
        Assert.Equal("1.0.0", detail.Versions[0].Version);

        var missing = await client.GetDetailAsync("https://example.test", "acme.missing", ct);
        Assert.Null(missing);
    }

    [Fact]
    public void Plugin_install_link_parses_and_validates_uris()
    {
        var link = PluginInstallLink.BuildUri("cowork.device-login", "1.0.2", "https://octoterm.si/");
        Assert.Equal("octopus://plugin/install?id=cowork.device-login&version=1.0.2&server=https%3A%2F%2Foctoterm.si", link);

        Assert.True(PluginInstallLink.TryParse(new Uri(link), out var req, out var err));
        Assert.Null(err);
        Assert.NotNull(req);
        Assert.Equal("cowork.device-login", req!.PluginId);
        Assert.Equal("1.0.2", req.Version);
        Assert.Equal("https://octoterm.si", req.ServerUrl);

        // Missing server / version is valid
        Assert.True(PluginInstallLink.TryParse(new Uri("octopus://plugin/install?id=acme.csv"), out var minReq, out _));
        Assert.Equal("acme.csv", minReq!.PluginId);
        Assert.Null(minReq.Version);
        Assert.Null(minReq.ServerUrl);

        // Unsafe server schemes, missing id, or path traversal are rejected
        Assert.False(PluginInstallLink.TryParse(new Uri("octopus://plugin/install?id=acme.csv&server=file%3A%2F%2F%2Fetc%2Fpasswd"), out _, out _));
        Assert.False(PluginInstallLink.TryParse(new Uri("octopus://plugin/install?version=1.0.0"), out _, out _));
        Assert.False(PluginInstallLink.TryParse(new Uri("octopus://plugin/install?id=..%2Fevil"), out _, out _));
        Assert.False(PluginInstallLink.TryParse(new Uri("octopus://bot/import?data=123"), out _, out _));
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
