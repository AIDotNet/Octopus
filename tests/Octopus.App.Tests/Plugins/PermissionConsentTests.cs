using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Plugins;
using Octopus.App.Plugins.Settings;
using Octopus.App.Services;
using Octopus.Core.Plugins;
using Octopus.Plugins;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Manifests;
using Octopus.Plugins.Marketplace;
using Octopus.Plugins.Packages;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Octopus.Plugins.Skills;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Plugins;

public sealed class PermissionConsentTests
{
    private static void SetLanguage(string? language) =>
        AppHost.Get<ISettingsStore>().Update(s => s.Appearance.Language = language);

    [AvaloniaFact]
    public void PermissionText_distinguishes_read_and_write_for_project_external_and_agent_data()
    {
        var previousLanguage = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
        try
        {
            SetLanguage("en-US");
            var projectRead = PermissionText.Describe(new PermissionRequest("fs.project", PermissionRisk.Medium, false, ["read"]));
            var projectWrite = PermissionText.Describe(new PermissionRequest("fs.project", PermissionRisk.High, false, ["readwrite"]));
            Assert.NotEqual(projectRead, projectWrite);
            Assert.Contains("Read", projectRead, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("change", projectWrite, StringComparison.OrdinalIgnoreCase);

            var externalRead = PermissionText.Describe(new PermissionRequest("fs.external", PermissionRisk.High, false, ["~/docs/**", "read"]));
            var externalWrite = PermissionText.Describe(new PermissionRequest("fs.external", PermissionRisk.High, false, ["~/docs/**", "readwrite"]));
            Assert.NotEqual(externalRead, externalWrite);
            Assert.Contains("Read", externalRead, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("change", externalRead, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("change", externalWrite, StringComparison.OrdinalIgnoreCase);

            var agentRead = PermissionText.Describe(new PermissionRequest("fs.agentData.claude", PermissionRisk.Medium, false, ["read", "projects/**/*.jsonl"]));
            var agentWrite = PermissionText.Describe(new PermissionRequest("fs.agentData.claude", PermissionRisk.High, false, ["readwrite", "projects/**/*.jsonl"]));
            Assert.NotEqual(agentRead, agentWrite);
            Assert.Contains("Read", agentRead, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("change", agentRead, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("change", agentWrite, StringComparison.OrdinalIgnoreCase);

            SetLanguage("zh-CN");
            var externalReadZh = PermissionText.Describe(new PermissionRequest("fs.external", PermissionRisk.High, false, ["~/docs/**", "read"]));
            var externalWriteZh = PermissionText.Describe(new PermissionRequest("fs.external", PermissionRisk.High, false, ["~/docs/**", "readwrite"]));
            Assert.Contains("读取", externalReadZh);
            Assert.Contains("读写", externalWriteZh);

            var agentReadZh = PermissionText.Describe(new PermissionRequest("fs.agentData.claude", PermissionRisk.Medium, false, ["read", "projects/**/*.jsonl"]));
            var agentWriteZh = PermissionText.Describe(new PermissionRequest("fs.agentData.claude", PermissionRisk.High, false, ["readwrite", "projects/**/*.jsonl"]));
            Assert.Contains("读取", agentReadZh);
            Assert.Contains("读写", agentWriteZh);
        }
        finally
        {
            SetLanguage(previousLanguage);
        }
    }

    [AvaloniaFact]
    public void PermissionRowViewModel_marks_secrets_project_write_external_and_wildcard_net_as_high_risk()
    {
        var previousLanguage = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
        SetLanguage("en-US");
        try
        {
            var paths = AppHost.Get<PluginPaths>();
            var highDir = paths.PackageDirectoryOf("acme.highrisk");
            Directory.CreateDirectory(highDir);
            File.WriteAllText(Path.Combine(highDir, "plugin.json"), """
                {
                  "schema": 1,
                  "id": "acme.highrisk",
                  "name": "High Risk Plugin",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "permissions": {
                    "secrets": true,
                    "fs": {
                      "project": { "access": "readwrite" },
                      "external": [{ "path": "~/docs/**", "access": "read", "reason": "Read docs" }]
                    },
                    "net": { "hosts": ["*.example.test"] }
                  }
                }
                """);

            var medDir = paths.PackageDirectoryOf("acme.readonly");
            Directory.CreateDirectory(medDir);
            File.WriteAllText(Path.Combine(medDir, "plugin.json"), """
                {
                  "schema": 1,
                  "id": "acme.readonly",
                  "name": "Read Only Plugin",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "permissions": {
                    "fs": { "project": { "access": "read" } },
                    "net": { "hosts": ["api.example.test"] }
                  }
                }
                """);

            AppHost.Get<IPluginCatalog>().Refresh();
            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var vm = new PluginsSettingsViewModel(
                AppHost.Get<IPluginCatalog>(),
                AppHost.Get<IPluginStateStore>(),
                AppHost.Get<PluginManager>(),
                AppHost.Get<PluginInstaller>(),
                paths,
                AppHost.Get<IWindowAccessor>(),
                AppHost.Get<PluginThemes>(),
                AppHost.Get<PluginConfigStore>(),
                new PluginMarketplaceClient(http),
                AppHost.Get<PluginSkillInstaller>(),
                AppHost.Get<ISettingsStore>(),
                AppHost.Get<IProjectService>());
            try
            {
                var highRow = Assert.Single(vm.Installed, r => r.Id == "acme.highrisk");
                var highPerms = highRow.Permissions.ToDictionary(p => p.Key);
                Assert.Equal(BadgeTone.Danger, highPerms["secrets"].RiskTone);
                Assert.Equal(BadgeTone.Danger, highPerms["fs.project"].RiskTone);
                Assert.Equal(BadgeTone.Danger, highPerms["fs.external"].RiskTone);
                Assert.Equal(BadgeTone.Danger, highPerms["net"].RiskTone);

                var medRow = Assert.Single(vm.Installed, r => r.Id == "acme.readonly");
                var medPerms = medRow.Permissions.ToDictionary(p => p.Key);
                Assert.Equal(BadgeTone.Warning, medPerms["fs.project"].RiskTone);
                Assert.Equal(BadgeTone.Warning, medPerms["net"].RiskTone);
            }
            finally
            {
                vm.Dispose();
            }
        }
        finally
        {
            SetLanguage(previousLanguage);
        }
    }

    [AvaloniaTheory]
    [InlineData("low", "secrets", true, false)]
    [InlineData("low", "fs.project:readwrite", true, false)]
    [InlineData("medium", "fs.external", true, false)]
    [InlineData("low", "net:*.example.test", true, false)]
    [InlineData("low", "fs.project:read", false, true)]
    [InlineData("low", "git", false, true)]
    [InlineData("low", "clipboard.read", true, false)]
    [InlineData("low", "fs.agentData.claude", true, false)]
    [InlineData("low", "net:api.*.example.test", false, true)]
    [InlineData("low", "projects", false, false)]
    public void Marketplace_row_never_understates_risk_below_declared_capabilities(
        string serverRisk,
        string permission,
        bool expectedHigh,
        bool expectedMedium)
    {
        var summary = new MarketplacePluginSummary(
            "acme.demo", "acme", "Acme", true, "Demo", null, "Demo plugin", null,
            "utility", [], null, null, "MIT", 1, false, "1.0.0", DateTime.UnixEpoch);
        var version = new MarketplacePluginVersion(
            "acme.demo", "1.0.0", 0, ">=0.1.0", [permission], serverRisk,
            100, new string('a', 64), "https://example.test/demo.octoplugin", "approved",
            null, null, DateTime.UnixEpoch, DateTime.UnixEpoch);

        var row = new MarketplacePluginRowViewModel(new MarketplaceCatalogEntry(summary, version), null, null);

        Assert.Equal(expectedHigh, row.IsHighRisk);
        Assert.Equal(expectedMedium, row.IsMediumRisk);
        if (expectedHigh) Assert.Equal(BadgeTone.Danger, row.RiskTone);
        else if (expectedMedium) Assert.Equal(BadgeTone.Warning, row.RiskTone);
        else Assert.Equal(BadgeTone.Success, row.RiskTone);
    }

    [AvaloniaFact]
    public async Task Deep_link_install_requires_explicit_high_risk_consent_for_actual_staged_manifest()
    {
        var previousLanguage = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
        SetLanguage("en-US");
        var window = new ArcWindow { Width = 960, Height = 720, Content = new Grid() };
        window.Show();
        Frames.Pump();

        try
        {
            var zipBytes = BuildPackageZip("""
                {
                  "schema": 1,
                  "id": "acme.deeplink",
                  "name": "Deep Link Plugin",
                  "description": "Plugin with real high-risk permissions",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "permissions": {
                    "secrets": true,
                    "fs": {
                      "project": { "access": "readwrite" },
                      "external": [{ "path": "~/docs/**", "access": "read", "reason": "Read docs" }]
                    }
                  }
                }
                """);
            var sha = Convert.ToHexStringLower(SHA256.HashData(zipBytes));

            var detailJson = $$"""
                {
                  "plugin": {
                    "id": "acme.deeplink",
                    "publisherSlug": "acme",
                    "publisherName": "Acme",
                    "publisherVerified": true,
                    "name": "Deep Link Plugin",
                    "description": "Understated metadata",
                    "category": "utility",
                    "tags": ["test"],
                    "downloads": 5,
                    "featured": false,
                    "latestVersion": "1.0.0",
                    "updatedAt": "2026-11-25T00:00:00Z"
                  },
                  "readmeMd": "# Deep Link Plugin",
                  "versions": [
                    {
                      "pluginId": "acme.deeplink",
                      "version": "1.0.0",
                      "apiVersion": 0,
                      "permissions": [],
                      "riskLevel": "low",
                      "packageSize": {{zipBytes.Length}},
                      "sha256": "{{sha}}",
                      "downloadUrl": "https://example.test/plugins/download/acme.deeplink/1.0.0.octoplugin",
                      "reviewStatus": "approved",
                      "submittedAt": "2026-11-25T00:00:00Z",
                      "publishedAt": "2026-11-25T00:00:00Z"
                    }
                  ]
                }
                """;

            using var http = new HttpClient(new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == "/api/plugins/acme.deeplink")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(detailJson, Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
            }));

            var accessor = new FixedWindowAccessor { Window = window };
            var catalog = AppHost.Get<IPluginCatalog>();
            var state = AppHost.Get<IPluginStateStore>();
            var installer = AppHost.Get<PluginInstaller>();
            var manager = AppHost.Get<PluginManager>();
            var deepLink = new PluginDeepLinkInstaller(
                new PluginMarketplaceClient(http),
                installer,
                catalog,
                state,
                manager,
                accessor,
                AppHost.Get<ISettingsStore>());

            var uri = new Uri(PluginInstallLink.BuildUri("acme.deeplink", "1.0.0", "https://example.test"));
            var handleTask = deepLink.HandleInstallUriAsync(uri);

            List<DialogSurface> OpenDialogs() =>
                window.GetVisualDescendants().OfType<DialogSurface>().ToList();

            Assert.True(Frames.Until(() => OpenDialogs().Count == 1), "Deep-link dialog did not open.");
            var deepLinkSurface = OpenDialogs().Single();
            var installButton = deepLinkSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .Single(b => b.Variant == ButtonVariant.Primary);

            installButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

            // After staging the downloaded package, a second consent dialog must open for the ACTUAL manifest permissions.
            var openedConsent = Frames.Until(() => OpenDialogs().Count >= 2);
            Assert.True(openedConsent, "Deep-link install committed without confirming the actual staged manifest permissions.");

            var consentSurface = OpenDialogs().Last();
            var consentText = string.Join("\n", consentSurface.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("Keep its own secrets (high risk)", consentText);
            Assert.Contains("Read and change files of the open project (high risk)", consentText);
            Assert.Contains("Read ~/docs/** (high risk)", consentText);
            var dangerConfirm = consentSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .FirstOrDefault(b => b.Variant == ButtonVariant.Danger);
            Assert.NotNull(dangerConfirm);

            // Cancel the consent dialog and close the outer dialog: plugin must NOT be installed.
            var consentCancel = consentSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .First(b => b.Variant == ButtonVariant.Secondary);
            consentCancel.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Frames.Pump();
            var deepLinkCancel = deepLinkSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .First(b => b.Variant == ButtonVariant.Secondary);
            deepLinkCancel.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.True(Frames.Until(() => handleTask.IsCompleted));
            await handleTask;

            Assert.Null(state.Current.Find("acme.deeplink"));
            Assert.False(Directory.Exists(AppHost.Get<PluginPaths>().PackageDirectoryOf("acme.deeplink")));
        }
        finally
        {
            SetLanguage(previousLanguage);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PermissionText_discloses_project_include_exclude_and_agent_data_include_scopes()
    {
        var previousLanguage = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
        try
        {
            var parsed = PluginManifestReader.Parse("""
                {
                  "schema": 1,
                  "id": "acme.scoped",
                  "name": "Scoped",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "permissions": {
                    "fs": {
                      "project": { "access": "read", "include": ["src/**/*.ts"], "exclude": ["**/secrets/**"] },
                      "agentData": [{ "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "read" }]
                    }
                  }
                }
                """, null, PluginSource.File);
            Assert.True(parsed.IsValid, string.Join("\n", parsed.Issues));
            var projectReq = Assert.Single(parsed.Manifest!.Permissions.Requests, r => r.Key == "fs.project");
            var agentReq = Assert.Single(parsed.Manifest.Permissions.Requests, r => r.Key == "fs.agentData.claude");

            SetLanguage("en-US");
            var projectEn = PermissionText.Describe(projectReq);
            var agentEn = PermissionText.Describe(agentReq);
            Assert.Contains("src/**/*.ts", projectEn);
            Assert.Contains("**/secrets/**", projectEn);
            Assert.Contains("projects/**/*.jsonl", agentEn);

            SetLanguage("zh-CN");
            var projectZh = PermissionText.Describe(projectReq);
            var agentZh = PermissionText.Describe(agentReq);
            Assert.Contains("src/**/*.ts", projectZh);
            Assert.Contains("**/secrets/**", projectZh);
            Assert.Contains("projects/**/*.jsonl", agentZh);
        }
        finally
        {
            SetLanguage(previousLanguage);
        }
    }

    [AvaloniaFact]
    public void Marketplace_detail_does_not_claim_purely_sandboxed_when_empty_permissions_carry_elevated_risk()
    {
        var previousLanguage = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
        SetLanguage("en-US");
        var window = new Window { Width = 640, Height = 640 };
        window.Show();
        try
        {
            var summary = new MarketplacePluginSummary(
                "acme.elevated", "acme", "Acme", true, "Elevated", null, "Elevated risk", null,
                "utility", [], null, null, "MIT", 1, false, "1.0.0", DateTime.UnixEpoch);
            var highVersion = new MarketplacePluginVersion(
                "acme.elevated", "1.0.0", 0, ">=0.1.0", [], "high",
                100, new string('a', 64), "https://example.test/elevated.octoplugin", "approved",
                null, null, DateTime.UnixEpoch, DateTime.UnixEpoch);

            var highRow = new MarketplacePluginRowViewModel(new MarketplaceCatalogEntry(summary, highVersion), null, null);
            var view = new MarketplacePluginDetailView { DataContext = highRow };
            window.Content = view;
            Frames.Pump();

            var visibleTexts = view.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text)
                .ToList();

            Assert.DoesNotContain(PluginStrings.MarketDetailNoPermissions, visibleTexts);
        }
        finally
        {
            window.Close();
            SetLanguage(previousLanguage);
        }
    }

    [AvaloniaFact]
    public async Task Required_and_optional_scopes_sharing_a_key_stay_independent_and_upgrades_do_not_broaden_optional_grants()
    {
        var catalog = AppHost.Get<IPluginCatalog>();
        var state = AppHost.Get<IPluginStateStore>();
        var installer = AppHost.Get<PluginInstaller>();
        var manager = AppHost.Get<PluginManager>();
        var paths = AppHost.Get<PluginPaths>();
        var pluginId = $"acme.reconcile.{Guid.NewGuid():N}"[..24];

        var tempV1 = Path.Combine(Path.GetTempPath(), $"{pluginId}-v1.octoplugin");
        var tempV2 = Path.Combine(Path.GetTempPath(), $"{pluginId}-v2.octoplugin");
        try
        {
            File.WriteAllBytes(tempV1, BuildPackageZip($$"""
                {
                  "schema": 1,
                  "id": "{{pluginId}}",
                  "name": "Reconcile Plugin",
                  "description": "v1",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "main": "main.js",
                  "contributes": { "commands": [{ "id": "{{pluginId}}.scopes", "title": "Scopes" }] },
                  "permissions": {
                    "net": { "hosts": ["api.example.test"], "optional": true },
                    "fs": {
                      "project": { "access": "read", "include": ["src/**"], "exclude": ["**/secrets/**"], "optional": true },
                      "external": [
                        { "path": "~/docs/**", "access": "read", "reason": "Required docs" },
                        { "path": "~/private/**", "access": "readwrite", "reason": "Optional private", "optional": true },
                        { "path": "~/kept/**", "access": "read", "reason": "Optional kept", "optional": true },
                        { "path": "~/widened/**", "access": "read", "reason": "Optional widened", "optional": true }
                      ],
                      "agentData": [
                        { "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "read" },
                        { "agent": "claude", "include": ["settings.json"], "access": "readwrite", "optional": true }
                      ]
                    }
                  }
                }
                """, ("main.js", $$"""
                import { commands, fs } from 'octopus';
                export function activate(ctx) {
                  ctx.subscriptions.push(commands.register('{{pluginId}}.scopes', () => fs.scopes()));
                }
                """)));

            var stagedV1 = installer.Stage(tempV1);
            await PluginDeepLinkInstaller.CommitStagedInstallAsync(manager, installer, state, stagedV1);

            // Inspect real FsModule effective scopes immediately after fresh install of v1.
            var hostV1 = manager.Find(pluginId);
            Assert.NotNull(hostV1);
            var fsScopesV1 = await ReadEffectiveFsScopesAsync(manager, $"{pluginId}.scopes");
            Assert.Contains(fsScopesV1, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "read");
            Assert.DoesNotContain(fsScopesV1, s => s.Kind == "external" && s.Root.EndsWith("private", StringComparison.Ordinal));
            Assert.DoesNotContain(fsScopesV1, s => s.Kind == "external" && s.Root.EndsWith("kept", StringComparison.Ordinal));
            Assert.DoesNotContain(fsScopesV1, s => s.Kind == "agentdata" && s.Access == "readwrite");

            // Grant v1's optional permissions via Settings (same path real users use).
            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var vm = new PluginsSettingsViewModel(
                catalog, state, manager, installer, paths, AppHost.Get<IWindowAccessor>(),
                AppHost.Get<PluginThemes>(), AppHost.Get<PluginConfigStore>(), new PluginMarketplaceClient(http),
                AppHost.Get<PluginSkillInstaller>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IProjectService>());
            try
            {
                var row = Assert.Single(vm.Installed, r => r.Id == pluginId);
                // Required external is granted; optional external is NOT granted even though both share key "fs.external".
                var externalRows = row.Permissions.Where(p => p.Key == "fs.external").ToList();
                Assert.True(externalRows[0].IsGranted);
                Assert.False(externalRows[1].IsGranted);

                // User explicitly grants optional net, fs.project, ~/kept/** (read), and ~/widened/** (read).
                vm.SetPermission(row, hostV1!.Manifest.Permissions.Requests.Single(r => r.Key == "net"), true);
                vm.SetPermission(row, hostV1.Manifest.Permissions.Requests.Single(r => r.Key == "fs.project"), true);
                vm.SetPermission(row, hostV1.Manifest.Permissions.Requests.Single(r => r.Key == "fs.external" && r.Details[0] == "~/kept/**"), true);
                vm.SetPermission(row, hostV1.Manifest.Permissions.Requests.Single(r => r.Key == "fs.external" && r.Details[0] == "~/widened/**"), true);
            }
            finally
            {
                vm.Dispose();
            }

            // Upgrade to v2:
            // - ~/kept/** (read, optional) stays compatible -> must be preserved
            // - ~/widened/** changes read -> readwrite (optional) -> must NOT be silently broadened
            // - net adds *.evil.test (optional) -> *.evil.test must NOT become effective
            // - fs.project removes exclude & broadens include (optional) -> must NOT stay silently granted
            File.WriteAllBytes(tempV2, BuildPackageZip($$"""
                {
                  "schema": 1,
                  "id": "{{pluginId}}",
                  "name": "Reconcile Plugin",
                  "description": "v2",
                  "version": "2.0.0",
                  "engines": { "api": 0 },
                  "main": "main.js",
                  "contributes": { "commands": [{ "id": "{{pluginId}}.scopes", "title": "Scopes" }] },
                  "permissions": {
                    "net": { "hosts": ["api.example.test", "*.evil.test"], "optional": true },
                    "fs": {
                      "project": { "access": "read", "include": ["**/*"], "optional": true },
                      "external": [
                        { "path": "~/docs/**", "access": "read", "reason": "Required docs" },
                        { "path": "~/kept/**", "access": "read", "reason": "Optional kept", "optional": true },
                        { "path": "~/widened/**", "access": "readwrite", "reason": "Optional widened", "optional": true }
                      ],
                      "agentData": [
                        { "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "read" }
                      ]
                    }
                  }
                }
                """, ("main.js", $$"""
                import { commands, fs } from 'octopus';
                export function activate(ctx) {
                  ctx.subscriptions.push(commands.register('{{pluginId}}.scopes', () => fs.scopes()));
                }
                """)));

            var stagedV2 = installer.Stage(tempV2);
            await PluginDeepLinkInstaller.CommitStagedInstallAsync(manager, installer, state, stagedV2);

            var hostV2 = manager.Find(pluginId);
            Assert.NotNull(hostV2);
            var fsScopesV2 = await ReadEffectiveFsScopesAsync(manager, $"{pluginId}.scopes");

            // Compatible optional grant preserved:
            Assert.Contains(fsScopesV2, s => s.Kind == "external" && s.Root.EndsWith("kept", StringComparison.Ordinal) && s.Access == "read");
            // Broadened optional grants NOT silently applied:
            Assert.DoesNotContain(fsScopesV2, s => s.Kind == "external" && s.Root.EndsWith("widened", StringComparison.Ordinal) && s.Access == "readwrite");
            Assert.DoesNotContain(fsScopesV2, s => s.Kind == "project");
            Assert.False(NetModule.HostAllowed("sub.evil.test", hostV2!.Grants.Details("net")));
        }
        finally
        {
            await manager.SetEnabledAsync(pluginId, false);
            installer.Uninstall(pluginId, removeData: true);
            state.Update(d => d.Plugins.Remove(pluginId));
            catalog.Refresh();
            manager.SyncHosts();
            if (File.Exists(tempV1)) File.Delete(tempV1);
            if (File.Exists(tempV2)) File.Delete(tempV2);
        }
    }

    [AvaloniaFact]
    public async Task Deep_link_upgrade_with_only_optional_permissions_opens_staged_confirmation_dialog()
    {
        var previousLanguage = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
        SetLanguage("en-US");
        var window = new ArcWindow { Width = 960, Height = 720, Content = new Grid() };
        window.Show();
        Frames.Pump();

        var catalog = AppHost.Get<IPluginCatalog>();
        var state = AppHost.Get<IPluginStateStore>();
        var installer = AppHost.Get<PluginInstaller>();
        var manager = AppHost.Get<PluginManager>();
        var pluginId = $"acme.optdl.{Guid.NewGuid():N}"[..20];
        var tempV1 = Path.Combine(Path.GetTempPath(), $"{pluginId}-v1.octoplugin");

        try
        {
            // Install v1 with an optional external read permission, then grant it.
            File.WriteAllBytes(tempV1, BuildPackageZip($$"""
                {
                  "schema": 1,
                  "id": "{{pluginId}}",
                  "name": "Optional Upgrade Plugin",
                  "description": "v1",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "permissions": {
                    "fs": {
                      "external": [{ "path": "~/docs/**", "access": "read", "reason": "Read docs", "optional": true }]
                    }
                  }
                }
                """));
            var stagedV1 = installer.Stage(tempV1);
            await PluginDeepLinkInstaller.CommitStagedInstallAsync(manager, installer, state, stagedV1);
            state.Update(d => d.Plugins[pluginId].Grants["fs.external"] = "~/docs/**");

            // Deep-link v2 has ONLY optional permissions (compatible ~/docs/** + broadened ~/secrets/**).
            var zipV2 = BuildPackageZip($$"""
                {
                  "schema": 1,
                  "id": "{{pluginId}}",
                  "name": "Optional Upgrade Plugin",
                  "description": "v2 optional only",
                  "version": "2.0.0",
                  "engines": { "api": 0 },
                  "permissions": {
                    "fs": {
                      "external": [
                        { "path": "~/docs/**", "access": "read", "reason": "Read docs", "optional": true },
                        { "path": "~/secrets/**", "access": "readwrite", "reason": "Write secrets", "optional": true }
                      ]
                    }
                  }
                }
                """);
            var shaV2 = Convert.ToHexStringLower(SHA256.HashData(zipV2));
            var detailJson = $$"""
                {
                  "plugin": {
                    "id": "{{pluginId}}",
                    "publisherSlug": "acme",
                    "publisherName": "Acme",
                    "publisherVerified": true,
                    "name": "Optional Upgrade Plugin",
                    "description": "v2 optional only",
                    "category": "utility",
                    "tags": ["test"],
                    "downloads": 5,
                    "featured": false,
                    "latestVersion": "2.0.0",
                    "updatedAt": "2026-11-25T00:00:00Z"
                  },
                  "readmeMd": "# Optional Upgrade",
                  "versions": [
                    {
                      "pluginId": "{{pluginId}}",
                      "version": "2.0.0",
                      "apiVersion": 0,
                      "permissions": [],
                      "riskLevel": "low",
                      "packageSize": {{zipV2.Length}},
                      "sha256": "{{shaV2}}",
                      "downloadUrl": "https://example.test/plugins/download/{{pluginId}}/2.0.0.octoplugin",
                      "reviewStatus": "approved",
                      "submittedAt": "2026-11-25T00:00:00Z",
                      "publishedAt": "2026-11-25T00:00:00Z"
                    }
                  ]
                }
                """;

            using var http = new HttpClient(new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == $"/api/plugins/{pluginId}")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(detailJson, Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipV2) };
            }));

            var accessor = new FixedWindowAccessor { Window = window };
            var deepLink = new PluginDeepLinkInstaller(
                new PluginMarketplaceClient(http), installer, catalog, state, manager, accessor, AppHost.Get<ISettingsStore>());
            var handleTask = deepLink.HandleInstallUriAsync(
                new Uri(PluginInstallLink.BuildUri(pluginId, "2.0.0", "https://example.test")));

            List<DialogSurface> OpenDialogs() =>
                window.GetVisualDescendants().OfType<DialogSurface>().ToList();

            Assert.True(Frames.Until(() => OpenDialogs().Count == 1), "Deep-link dialog did not open.");
            var deepLinkSurface = OpenDialogs().Single();
            var installButton = deepLinkSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .Single(b => b.Variant == ButtonVariant.Primary);
            installButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

            // Staged confirmation dialog MUST open even when v2 has only optional requests.
            var openedConsent = Frames.Until(() => OpenDialogs().Count >= 2);
            Assert.True(openedConsent, "Optional-only deep-link upgrade skipped the staged confirmation dialog.");

            var consentSurface = OpenDialogs().Last();
            var consentText = string.Join("\n", consentSurface.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("~/docs/**", consentText);
            Assert.Contains("~/secrets/**", consentText);

            var consentCancel = consentSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .First(b => b.Variant == ButtonVariant.Secondary);
            consentCancel.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Frames.Pump();
            var deepLinkCancel = deepLinkSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .First(b => b.Variant == ButtonVariant.Secondary);
            deepLinkCancel.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.True(Frames.Until(() => handleTask.IsCompleted));
            await handleTask;

            // Version must remain 1.0.0 after cancellation.
            Assert.Equal("1.0.0", state.Current.Find(pluginId)?.Version);
        }
        finally
        {
            await manager.SetEnabledAsync(pluginId, false);
            installer.Uninstall(pluginId, removeData: true);
            state.Update(d => d.Plugins.Remove(pluginId));
            catalog.Refresh();
            manager.SyncHosts();
            if (File.Exists(tempV1)) File.Delete(tempV1);
            SetLanguage(previousLanguage);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Fresh_deep_link_install_with_surviving_record_agrees_with_dialog_and_does_not_activate_optional_grants()
    {
        var previousLanguage = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
        SetLanguage("en-US");
        var window = new ArcWindow { Width = 960, Height = 720, Content = new Grid() };
        window.Show();
        Frames.Pump();

        var catalog = AppHost.Get<IPluginCatalog>();
        var state = AppHost.Get<IPluginStateStore>();
        var installer = AppHost.Get<PluginInstaller>();
        var manager = AppHost.Get<PluginManager>();
        var pluginId = $"acme.fresh.{Guid.NewGuid():N}"[..20];

        try
        {
            // Seed a surviving state record (e.g., from a previous dev copy or deleted package) when NO package is installed.
            state.Update(d => d.Plugins[pluginId] = new PluginRecord
            {
                Enabled = true,
                Version = "1.0.0",
                Source = PluginSource.Dev,
                Grants = { ["fs.external"] = "~/kept/**", ["net"] = "api.example.test" },
                ScopedGrants =
                {
                    new PluginPermissionGrant("fs.external", "read", ["~/kept/**"]),
                    new PluginPermissionGrant("net", "granted", ["api.example.test"]),
                },
            });

            var zipBytes = BuildPackageZip($$"""
                {
                  "schema": 1,
                  "id": "{{pluginId}}",
                  "name": "Fresh Consent Plugin",
                  "description": "Fresh install with optional permissions",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "main": "main.js",
                  "contributes": { "commands": [{ "id": "{{pluginId}}.scopes", "title": "Scopes" }] },
                  "permissions": {
                    "net": { "hosts": ["api.example.test"], "optional": true },
                    "fs": {
                      "external": [
                        { "path": "~/docs/**", "access": "read", "reason": "Required docs" },
                        { "path": "~/kept/**", "access": "read", "reason": "Optional kept", "optional": true }
                      ]
                    }
                  }
                }
                """, ("main.js", $$"""
                import { commands, fs } from 'octopus';
                export function activate(ctx) {
                  ctx.subscriptions.push(commands.register('{{pluginId}}.scopes', () => fs.scopes()));
                }
                """));
            var sha = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            var detailJson = $$"""
                {
                  "plugin": {
                    "id": "{{pluginId}}",
                    "publisherSlug": "acme",
                    "publisherName": "Acme",
                    "publisherVerified": true,
                    "name": "Fresh Consent Plugin",
                    "description": "Fresh install with optional permissions",
                    "category": "utility",
                    "tags": ["test"],
                    "downloads": 5,
                    "featured": false,
                    "latestVersion": "1.0.0",
                    "updatedAt": "2026-11-25T00:00:00Z"
                  },
                  "readmeMd": "# Fresh Consent",
                  "versions": [
                    {
                      "pluginId": "{{pluginId}}",
                      "version": "1.0.0",
                      "apiVersion": 0,
                      "permissions": [],
                      "riskLevel": "low",
                      "packageSize": {{zipBytes.Length}},
                      "sha256": "{{sha}}",
                      "downloadUrl": "https://example.test/plugins/download/{{pluginId}}/1.0.0.octoplugin",
                      "reviewStatus": "approved",
                      "submittedAt": "2026-11-25T00:00:00Z",
                      "publishedAt": "2026-11-25T00:00:00Z"
                    }
                  ]
                }
                """;

            using var http = new HttpClient(new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == $"/api/plugins/{pluginId}")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(detailJson, Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
            }));

            var accessor = new FixedWindowAccessor { Window = window };
            var deepLink = new PluginDeepLinkInstaller(
                new PluginMarketplaceClient(http), installer, catalog, state, manager, accessor, AppHost.Get<ISettingsStore>());
            var handleTask = deepLink.HandleInstallUriAsync(
                new Uri(PluginInstallLink.BuildUri(pluginId, "1.0.0", "https://example.test")));

            List<DialogSurface> OpenDialogs() =>
                window.GetVisualDescendants().OfType<DialogSurface>().ToList();

            Assert.True(Frames.Until(() => OpenDialogs().Count == 1), "Deep-link dialog did not open.");
            var deepLinkSurface = OpenDialogs().Single();
            var installButton = deepLinkSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .Single(b => b.Variant == ButtonVariant.Primary);
            installButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

            Assert.True(Frames.Until(() => OpenDialogs().Count >= 2), "Consent dialog did not open.");
            var consentSurface = OpenDialogs().Last();
            var consentText = string.Join("\n", consentSurface.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("Optional permissions (not granted yet; asked only when used):", consentText);
            Assert.DoesNotContain("Previously granted optional permissions kept:", consentText);

            // Confirm the fresh install dialog.
            var confirmButton = consentSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .First(b => b.Variant is ButtonVariant.Danger or ButtonVariant.Primary);
            confirmButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

            Assert.True(Frames.Until(() => OpenDialogs().Count == 1 && manager.Find(pluginId) is not null), "Install did not complete.");

            var closeButton = deepLinkSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                .First(b => b.Variant == ButtonVariant.Secondary);
            closeButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.True(Frames.Until(() => handleTask.IsCompleted));
            await handleTask;

            // Committed grants MUST match the fresh-install dialog: required docs granted, optional kept/net NOT granted.
            var host = manager.Find(pluginId);
            Assert.NotNull(host);
            Assert.Empty(host!.Grants.Details("net"));
            var fsScopes = await ReadEffectiveFsScopesAsync(manager, $"{pluginId}.scopes");
            Assert.Contains(fsScopes, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "read");
            Assert.DoesNotContain(fsScopes, s => s.Kind == "external" && s.Root.EndsWith("kept", StringComparison.Ordinal));
        }
        finally
        {
            await manager.SetEnabledAsync(pluginId, false);
            installer.Uninstall(pluginId, removeData: true);
            state.Update(d => d.Plugins.Remove(pluginId));
            catalog.Refresh();
            manager.SyncHosts();
            SetLanguage(previousLanguage);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Settings_fresh_marketplace_install_with_surviving_record_agrees_with_dialog_and_does_not_activate_optional_grants()
    {
        var previousLanguage = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
        var settings = AppHost.Get<ISettingsStore>();
        var previousRelay = settings.Current.Remote.RelayUrl;
        SetLanguage("en-US");
        var window = new ArcWindow { Width = 960, Height = 720, Content = new Grid() };
        window.Show();
        Frames.Pump();

        var catalog = AppHost.Get<IPluginCatalog>();
        var state = AppHost.Get<IPluginStateStore>();
        var installer = AppHost.Get<PluginInstaller>();
        var manager = AppHost.Get<PluginManager>();
        var paths = AppHost.Get<PluginPaths>();
        var pluginId = $"acme.setfr.{Guid.NewGuid():N}"[..20];

        try
        {
            settings.Update(s => s.Remote.RelayUrl = "https://example.test");
            state.Update(d => d.Plugins[pluginId] = new PluginRecord
            {
                Enabled = true,
                Version = "1.0.0",
                Source = PluginSource.File,
                Grants = { ["fs.external"] = "~/kept/**", ["net"] = "api.example.test" },
                ScopedGrants =
                {
                    new PluginPermissionGrant("fs.external", "read", ["~/kept/**"]),
                    new PluginPermissionGrant("net", "granted", ["api.example.test"]),
                },
            });

            var zipBytes = BuildPackageZip($$"""
                {
                  "schema": 1,
                  "id": "{{pluginId}}",
                  "name": "Settings Fresh Plugin",
                  "description": "Fresh install via settings",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "main": "main.js",
                  "contributes": { "commands": [{ "id": "{{pluginId}}.scopes", "title": "Scopes" }] },
                  "permissions": {
                    "net": { "hosts": ["api.example.test"], "optional": true },
                    "fs": {
                      "external": [
                        { "path": "~/docs/**", "access": "read", "reason": "Required docs" },
                        { "path": "~/kept/**", "access": "read", "reason": "Optional kept", "optional": true }
                      ]
                    }
                  }
                }
                """, ("main.js", $$"""
                import { commands, fs } from 'octopus';
                export function activate(ctx) {
                  ctx.subscriptions.push(commands.register('{{pluginId}}.scopes', () => fs.scopes()));
                }
                """));
            var sha = Convert.ToHexStringLower(SHA256.HashData(zipBytes));

            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes),
            }));
            var accessor = new FixedWindowAccessor { Window = window };
            var vm = new PluginsSettingsViewModel(
                catalog, state, manager, installer, paths, accessor,
                AppHost.Get<PluginThemes>(), AppHost.Get<PluginConfigStore>(), new PluginMarketplaceClient(http),
                AppHost.Get<PluginSkillInstaller>(), settings, AppHost.Get<IProjectService>());
            try
            {
                var summary = new MarketplacePluginSummary(
                    pluginId, "acme", "Acme", true, "Settings Fresh Plugin", null, "Fresh install via settings", null,
                    "utility", [], null, null, "MIT", 1, false, "1.0.0", DateTime.UnixEpoch);
                var version = new MarketplacePluginVersion(
                    pluginId, "1.0.0", 0, ">=0.1.0", ["fs.external", "net"], "high",
                    zipBytes.Length, sha, $"https://example.test/plugins/download/{pluginId}/1.0.0.octoplugin", "approved",
                    null, null, DateTime.UnixEpoch, DateTime.UnixEpoch);
                var row = new MarketplacePluginRowViewModel(new MarketplaceCatalogEntry(summary, version), null, null);

                var installTask = vm.InstallFromMarketplaceAsync(row);
                List<DialogSurface> OpenDialogs() =>
                    window.GetVisualDescendants().OfType<DialogSurface>().ToList();

                Assert.True(Frames.Until(() => OpenDialogs().Count == 1), "Settings consent dialog did not open.");
                var consentSurface = OpenDialogs().Single();
                var consentText = string.Join("\n", consentSurface.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                Assert.Contains("Optional permissions (not granted yet; asked only when used):", consentText);
                Assert.DoesNotContain("Previously granted optional permissions kept:", consentText);

                var confirmButton = consentSurface.GetVisualDescendants().OfType<TKCode.Arc.Button>()
                    .First(b => b.Variant is ButtonVariant.Danger or ButtonVariant.Primary);
                confirmButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

                Assert.True(Frames.Until(() => installTask.IsCompleted));
                await installTask;

                var host = manager.Find(pluginId);
                Assert.NotNull(host);
                Assert.Empty(host!.Grants.Details("net"));
                var fsScopes = await ReadEffectiveFsScopesAsync(manager, $"{pluginId}.scopes");
                Assert.Contains(fsScopes, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "read");
                Assert.DoesNotContain(fsScopes, s => s.Kind == "external" && s.Root.EndsWith("kept", StringComparison.Ordinal));
            }
            finally
            {
                vm.Dispose();
            }
        }
        finally
        {
            await manager.SetEnabledAsync(pluginId, false);
            installer.Uninstall(pluginId, removeData: true);
            state.Update(d => d.Plugins.Remove(pluginId));
            catalog.Refresh();
            manager.SyncHosts();
            settings.Update(s => s.Remote.RelayUrl = previousRelay);
            SetLanguage(previousLanguage);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Settings_revoking_optional_write_keeps_required_read_on_same_external_path()
    {
        var catalog = AppHost.Get<IPluginCatalog>();
        var state = AppHost.Get<IPluginStateStore>();
        var installer = AppHost.Get<PluginInstaller>();
        var manager = AppHost.Get<PluginManager>();
        var paths = AppHost.Get<PluginPaths>();
        var pluginId = $"acme.samepath.{Guid.NewGuid():N}"[..22];
        var tempPkg = Path.Combine(Path.GetTempPath(), $"{pluginId}.octoplugin");

        try
        {
            File.WriteAllBytes(tempPkg, BuildPackageZip($$"""
                {
                  "schema": 1,
                  "id": "{{pluginId}}",
                  "name": "Same Path Plugin",
                  "description": "Required read and optional write on same target",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "main": "main.js",
                  "contributes": { "commands": [{ "id": "{{pluginId}}.scopes", "title": "Scopes" }] },
                  "permissions": {
                    "fs": {
                      "external": [
                        { "path": "~/docs/**", "access": "read", "reason": "Read docs" },
                        { "path": "~/docs/**", "access": "readwrite", "reason": "Edit docs", "optional": true }
                      ]
                    }
                  }
                }
                """, ("main.js", $$"""
                import { commands, fs } from 'octopus';
                export function activate(ctx) {
                  ctx.subscriptions.push(commands.register('{{pluginId}}.scopes', () => fs.scopes()));
                }
                """)));

            var staged = installer.Stage(tempPkg);
            await PluginDeepLinkInstaller.CommitStagedInstallAsync(manager, installer, state, staged);

            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var vm = new PluginsSettingsViewModel(
                catalog, state, manager, installer, paths, AppHost.Get<IWindowAccessor>(),
                AppHost.Get<PluginThemes>(), AppHost.Get<PluginConfigStore>(), new PluginMarketplaceClient(http),
                AppHost.Get<PluginSkillInstaller>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IProjectService>());
            try
            {
                var row = Assert.Single(vm.Installed, r => r.Id == pluginId);
                var host = manager.Find(pluginId)!;
                var optWrite = host.Manifest.Permissions.Requests.Single(r => r.Key == "fs.external" && r.Optional);

                // Decline optional readwrite directly from initial state: required read must remain granted.
                vm.SetPermission(row, optWrite, granted: false);
                row.RefreshState();
                var externalRows = row.Permissions.Where(p => p.Key == "fs.external").ToList();
                Assert.True(externalRows[0].IsGranted);
                Assert.False(externalRows[1].IsGranted);

                var scopesAfterDecline = await ReadEffectiveFsScopesAsync(manager, $"{pluginId}.scopes");
                Assert.Contains(scopesAfterDecline, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "read");
                Assert.DoesNotContain(scopesAfterDecline, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "readwrite");

                // Enable optional readwrite, then revoke it: required read must still remain granted.
                vm.SetPermission(row, optWrite, granted: true);
                var scopesWithWrite = await ReadEffectiveFsScopesAsync(manager, $"{pluginId}.scopes");
                Assert.Contains(scopesWithWrite, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "readwrite");

                vm.SetPermission(row, optWrite, granted: false);
                row.RefreshState();
                Assert.True(externalRows[0].IsGranted);
                Assert.False(externalRows[1].IsGranted);
                var scopesAfterRevoke = await ReadEffectiveFsScopesAsync(manager, $"{pluginId}.scopes");
                Assert.Contains(scopesAfterRevoke, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "read");
                Assert.DoesNotContain(scopesAfterRevoke, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "readwrite");
            }
            finally
            {
                vm.Dispose();
            }
        }
        finally
        {
            await manager.SetEnabledAsync(pluginId, false);
            installer.Uninstall(pluginId, removeData: true);
            state.Update(d => d.Plugins.Remove(pluginId));
            catalog.Refresh();
            manager.SyncHosts();
            if (File.Exists(tempPkg)) File.Delete(tempPkg);
        }
    }

    [AvaloniaFact]
    public async Task Settings_turning_off_optional_read_preserves_required_write_on_same_external_path()
    {
        var catalog = AppHost.Get<IPluginCatalog>();
        var state = AppHost.Get<IPluginStateStore>();
        var installer = AppHost.Get<PluginInstaller>();
        var manager = AppHost.Get<PluginManager>();
        var paths = AppHost.Get<PluginPaths>();
        var pluginId = $"acme.rwopt.{Guid.NewGuid():N}"[..22];
        var tempPkg = Path.Combine(Path.GetTempPath(), $"{pluginId}.octoplugin");

        try
        {
            File.WriteAllBytes(tempPkg, BuildPackageZip($$"""
                {
                  "schema": 1,
                  "id": "{{pluginId}}",
                  "name": "Required Write Optional Read Plugin",
                  "description": "Required readwrite and optional read on same target",
                  "version": "1.0.0",
                  "engines": { "api": 0 },
                  "main": "main.js",
                  "contributes": { "commands": [{ "id": "{{pluginId}}.scopes", "title": "Scopes" }] },
                  "permissions": {
                    "fs": {
                      "external": [
                        { "path": "~/docs/**", "access": "readwrite", "reason": "Edit docs" },
                        { "path": "~/docs/**", "access": "read", "reason": "Read docs", "optional": true }
                      ]
                    }
                  }
                }
                """, ("main.js", $$"""
                import { commands, fs } from 'octopus';
                export function activate(ctx) {
                  ctx.subscriptions.push(commands.register('{{pluginId}}.scopes', () => fs.scopes()));
                }
                """)));

            var staged = installer.Stage(tempPkg);
            await PluginDeepLinkInstaller.CommitStagedInstallAsync(manager, installer, state, staged);

            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var vm = new PluginsSettingsViewModel(
                catalog, state, manager, installer, paths, AppHost.Get<IWindowAccessor>(),
                AppHost.Get<PluginThemes>(), AppHost.Get<PluginConfigStore>(), new PluginMarketplaceClient(http),
                AppHost.Get<PluginSkillInstaller>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IProjectService>());
            try
            {
                var row = Assert.Single(vm.Installed, r => r.Id == pluginId);
                var externalRows = row.Permissions.Where(p => p.Key == "fs.external").ToList();
                Assert.True(externalRows[0].IsGranted);
                Assert.True(externalRows[1].IsGranted);
                Assert.True(externalRows[0].CanToggle);
                Assert.False(externalRows[1].CanToggle);

                externalRows[1].IsGranted = false;
                row.RefreshState();

                Assert.True(externalRows[0].IsGranted);
                var scopes = await ReadEffectiveFsScopesAsync(manager, $"{pluginId}.scopes");
                Assert.Contains(scopes, s => s.Kind == "external" && s.Root.EndsWith("docs", StringComparison.Ordinal) && s.Access == "readwrite");
            }
            finally
            {
                vm.Dispose();
            }
        }
        finally
        {
            await manager.SetEnabledAsync(pluginId, false);
            installer.Uninstall(pluginId, removeData: true);
            state.Update(d => d.Plugins.Remove(pluginId));
            catalog.Refresh();
            manager.SyncHosts();
            if (File.Exists(tempPkg)) File.Delete(tempPkg);
        }
    }

    [AvaloniaFact]
    public async Task Reload_narrowed_sole_permission_remains_revocable_across_second_reload_and_disk_round_trip()
    {
        var id = $"acme.narrow.{Guid.NewGuid():N}"[..20];
        var temp = Directory.CreateTempSubdirectory("sec03-narrow-").FullName;
        const string envName = "OCTOPUS_DEV_PLUGINS";
        var priorEnv = Environment.GetEnvironmentVariable(envName);
        var catalog = AppHost.Get<IPluginCatalog>();
        var manager = AppHost.Get<PluginManager>();
        var state = AppHost.Get<IPluginStateStore>();

        void WriteManifest(string mode) => File.WriteAllText(Path.Combine(temp, "plugin.json"), $$"""
            {
              "schema": 1,
              "id": "{{id}}",
              "name": "Narrow Sample",
              "version": "1.0.0",
              "engines": { "api": 0 },
              "main": "main.js",
              "permissions": {
                "fs": {
                  "external": [
                    { "path": "~/fixtures/review/**", "access": "{{mode}}", "reason": "Fixture", "optional": true }
                  ]
                }
              }
            }
            """);

        try
        {
            Environment.SetEnvironmentVariable(envName, temp);
            File.WriteAllText(Path.Combine(temp, "main.js"), "export function activate(ctx) {}");
            WriteManifest("readwrite");
            catalog.Refresh();
            Assert.Equal(PluginSource.Dev, catalog.Find(id)!.Source);
            await manager.SetEnabledAsync(id, true);

            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            using var vm = new PluginsSettingsViewModel(
                catalog, state, manager, AppHost.Get<PluginInstaller>(), AppHost.Get<PluginPaths>(), AppHost.Get<IWindowAccessor>(),
                AppHost.Get<PluginThemes>(), AppHost.Get<PluginConfigStore>(), new PluginMarketplaceClient(http),
                AppHost.Get<PluginSkillInstaller>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IProjectService>());

            var initialRow = Assert.Single(vm.Installed, p => p.Id == id);
            var initialPerm = Assert.Single(initialRow.Permissions);
            Assert.True(initialPerm.CanToggle);
            initialPerm.IsGranted = true;
            initialRow.RefreshState();
            Assert.True(initialPerm.IsGranted);

            // Narrow sole declaration from readwrite -> read and reload.
            WriteManifest("read");
            await manager.ReloadAsync(id);
            vm.Refresh();
            var narrowedRow = Assert.Single(vm.Installed, p => p.Id == id);
            var only = Assert.Single(narrowedRow.Permissions);
            Assert.Equal(FileAccessKind.Read, narrowedRow.Package.Manifest!.Permissions.FsExternal.Single().Access);
            Assert.True(only.IsGranted);
            Assert.True(only.CanToggle);

            // Revoke the sole remaining permission.
            only.IsGranted = false;
            narrowedRow.RefreshState();
            Assert.False(only.IsGranted);
            Assert.Empty(state.Current.Find(id)!.ScopedGrants);

            // Disk round trip + second reload (keeping read, then widening back to readwrite) must stay revoked.
            state.Load();
            await manager.ReloadAsync(id);
            vm.Refresh();
            var afterSecondReload = Assert.Single(vm.Installed, p => p.Id == id);
            var afterOnly = Assert.Single(afterSecondReload.Permissions);
            Assert.False(afterOnly.IsGranted);
            Assert.True(afterOnly.CanToggle);

            WriteManifest("readwrite");
            await manager.ReloadAsync(id);
            vm.Refresh();
            var restoredRow = Assert.Single(vm.Installed, p => p.Id == id);
            var restoredPerm = Assert.Single(restoredRow.Permissions);
            Assert.False(restoredPerm.IsGranted);
            Assert.True(restoredPerm.CanToggle);
        }
        finally
        {
            await manager.SetEnabledAsync(id, false);
            Environment.SetEnvironmentVariable(envName, priorEnv);
            state.Update(d => d.Plugins.Remove(id));
            catalog.Refresh();
            manager.SyncHosts();
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private sealed record EffectiveScope(string Kind, string Root, string Access);

    private static async Task<List<EffectiveScope>> ReadEffectiveFsScopesAsync(PluginManager manager, string commandId)
    {
        var result = await manager.ExecuteCommandAsync(commandId);
        Assert.NotNull(result);
        var arr = result!.AsArray();
        return arr.Select(n => new EffectiveScope(
            n!["kind"]!.GetValue<string>(),
            n["root"]!.GetValue<string>(),
            n["access"]!.GetValue<string>())).ToList();
    }

    private static byte[] BuildPackageZip(string manifestJson, params (string Path, string Content)[] extraFiles)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("plugin.json").Open())
            {
                entry.Write(Encoding.UTF8.GetBytes(manifestJson));
            }
            foreach (var (path, content) in extraFiles)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return ms.ToArray();
    }

    private sealed class FixedWindowAccessor : IWindowAccessor
    {
        public TopLevel? Window { get; set; }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
