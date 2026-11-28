using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Octopus.App.Composition;
using Octopus.App.Plugins;
using Octopus.App.Plugins.Settings;
using Octopus.App.Services;
using Octopus.Plugins;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Marketplace;
using Octopus.Plugins.Packages;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Octopus.Plugins.Skills;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests.Plugins;

public sealed class MarketplaceInstallPolicyTests
{
    private static readonly string ValidSha = Convert.ToHexStringLower(SHA256.HashData("bundled-plugin-bytes"u8));

    private static string CatalogJson => $$"""
        {
          "total": 3,
          "page": 1,
          "pageSize": 24,
          "items": [
            {
              "plugin": {
                "id": "acme.missing-sha",
                "publisherSlug": "acme",
                "publisherName": "Acme",
                "publisherVerified": true,
                "name": "Missing SHA",
                "description": "Missing digest metadata",
                "category": "utility",
                "tags": ["test"],
                "downloads": 100,
                "featured": false,
                "latestVersion": "1.0.0",
                "updatedAt": "2026-11-25T00:00:00Z"
              },
              "latest": {
                "pluginId": "acme.missing-sha",
                "version": "1.0.0",
                "apiVersion": 0,
                "permissions": [],
                "riskLevel": "low",
                "packageSize": 1024,
                "sha256": "",
                "downloadUrl": "https://example.test/missing.octoplugin",
                "reviewStatus": "approved",
                "submittedAt": "2026-11-25T00:00:00Z",
                "publishedAt": "2026-11-25T00:00:00Z"
              }
            },
            {
              "plugin": {
                "id": "acme.short-sha",
                "publisherSlug": "acme",
                "publisherName": "Acme",
                "publisherVerified": true,
                "name": "Short SHA",
                "description": "Malformed digest metadata",
                "category": "utility",
                "tags": ["test"],
                "downloads": 80,
                "featured": false,
                "latestVersion": "1.0.0",
                "updatedAt": "2026-11-25T00:00:00Z"
              },
              "latest": {
                "pluginId": "acme.short-sha",
                "version": "1.0.0",
                "apiVersion": 0,
                "permissions": [],
                "riskLevel": "low",
                "packageSize": 1024,
                "sha256": "abc123",
                "downloadUrl": "https://example.test/short.octoplugin",
                "reviewStatus": "approved",
                "submittedAt": "2026-11-25T00:00:00Z",
                "publishedAt": "2026-11-25T00:00:00Z"
              }
            },
            {
              "plugin": {
                "id": "acme.valid-sha",
                "publisherSlug": "acme",
                "publisherName": "Acme",
                "publisherVerified": true,
                "name": "Valid SHA",
                "description": "Complete digest metadata",
                "category": "utility",
                "tags": ["test"],
                "downloads": 60,
                "featured": false,
                "latestVersion": "1.0.0",
                "updatedAt": "2026-11-25T00:00:00Z"
              },
              "latest": {
                "pluginId": "acme.valid-sha",
                "version": "1.0.0",
                "apiVersion": 0,
                "permissions": [],
                "riskLevel": "low",
                "packageSize": 1024,
                "sha256": "{{ValidSha}}",
                "downloadUrl": "https://example.test/valid.octoplugin",
                "reviewStatus": "approved",
                "submittedAt": "2026-11-25T00:00:00Z",
                "publishedAt": "2026-11-25T00:00:00Z"
              }
            }
          ]
        }
        """;

    private static PluginsSettingsViewModel CreateViewModel(HttpClient http) => new(
        AppHost.Get<IPluginCatalog>(),
        AppHost.Get<IPluginStateStore>(),
        AppHost.Get<PluginManager>(),
        AppHost.Get<PluginInstaller>(),
        AppHost.Get<PluginPaths>(),
        AppHost.Get<IWindowAccessor>(),
        AppHost.Get<PluginThemes>(),
        AppHost.Get<PluginConfigStore>(),
        new PluginMarketplaceClient(http),
        AppHost.Get<PluginSkillInstaller>(),
        AppHost.Get<ISettingsStore>(),
        AppHost.Get<IProjectService>());

    [AvaloniaFact]
    public async Task Marketplace_cards_disable_install_when_metadata_lacks_valid_sha256()
    {
        var downloadCalls = 0;
        using var http = new HttpClient(new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/v1/plugins/catalog")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(CatalogJson, Encoding.UTF8, "application/json"),
                };
            }
            downloadCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("bundled-plugin-bytes"u8.ToArray()) };
        }));

        var vm = CreateViewModel(http);
        var page = new PluginsPageView { DataContext = vm };
        var window = new Window { Width = 960, Height = 720, Content = page };
        window.Show();
        try
        {
            vm.ActiveTab = "marketplace";
            await vm.RefreshMarketplaceCommand.ExecuteAsync(null);
            Frames.Pump();

            var missingRow = Assert.Single(vm.MarketItems, i => i.Id == "acme.missing-sha");
            var shortRow = Assert.Single(vm.MarketItems, i => i.Id == "acme.short-sha");
            var validRow = Assert.Single(vm.MarketItems, i => i.Id == "acme.valid-sha");

            var buttons = page.GetLogicalDescendants().OfType<TKCode.Arc.Button>().ToList();
            var missingButtons = buttons.Where(b => ReferenceEquals(b.DataContext, missingRow) && Equals(b.Content, missingRow.ActionLabel)).ToList();
            var shortButtons = buttons.Where(b => ReferenceEquals(b.DataContext, shortRow) && Equals(b.Content, shortRow.ActionLabel)).ToList();
            var validButtons = buttons.Where(b => ReferenceEquals(b.DataContext, validRow) && Equals(b.Content, validRow.ActionLabel)).ToList();

            Assert.NotEmpty(missingButtons);
            Assert.NotEmpty(shortButtons);
            Assert.NotEmpty(validButtons);
            Assert.All(missingButtons, b => Assert.False(b.IsEnabled));
            Assert.All(shortButtons, b => Assert.False(b.IsEnabled));
            Assert.All(validButtons, b => Assert.True(b.IsEnabled));

            await vm.InstallFromMarketplaceAsync(missingRow);
            Assert.Equal(0, downloadCalls);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Marketplace_refresh_rejects_external_http_endpoint_and_allows_localhost_dev()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var previousRelay = settings.Current.Remote.RelayUrl;
        var requests = 0;

        using var http = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CatalogJson, Encoding.UTF8, "application/json"),
            };
        }));

        var vm = CreateViewModel(http);
        try
        {
            settings.Update(s => s.Remote.RelayUrl = "http://marketplace.example.test");
            await vm.RefreshMarketplaceCommand.ExecuteAsync(null);

            Assert.Equal(0, requests);
            Assert.True(vm.HasMarketError);
            Assert.Empty(vm.MarketItems);

            settings.Update(s => s.Remote.RelayUrl = "http://127.0.0.1:5000");
            await vm.RefreshMarketplaceCommand.ExecuteAsync(null);

            Assert.Equal(1, requests);
            Assert.False(vm.HasMarketError);
            Assert.Equal(3, vm.MarketItems.Count);
        }
        finally
        {
            settings.Update(s => s.Remote.RelayUrl = previousRelay);
            vm.Dispose();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
