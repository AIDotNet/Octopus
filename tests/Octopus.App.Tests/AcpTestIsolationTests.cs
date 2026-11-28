using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Octopus.Acp.Process;
using Octopus.Agents;
using Octopus.App.AgentGui;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Settings;
using Octopus.Platform;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// Regression tests verifying that <see cref="TestApp"/> isolates ACP launch resolution, adapter installation,
/// registry access, and tab teardown from host binaries, login shells, public networks, and cross-test leaks.
/// </summary>
public class AcpTestIsolationTests
{
    [AvaloniaFact]
    public async Task AppHost_resolves_catalog_acp_adapters_offline_inside_test_cache_without_host_runners()
    {
        var resolver = AppHost.Get<AcpLaunchResolver>();
        var catalog = AppHost.Get<IAgentCatalog>();
        var scanner = AppHost.Get<IAgentScanner>();
        var paths = AppHost.Get<IAppPaths>();
        var cacheRoot = Path.GetFullPath(paths.CacheDirectory);

        foreach (var agentId in new[] { "claude", "codex" })
        {
            var definition = Assert.IsType<AgentDefinition>(catalog.Find(agentId));
            var installation = scanner.Find(agentId);

            // Must resolve offline even under NativeOnly so tests never depend on host npm/npx or installed providers.
            var nativeOnly = new AgentSettings { AcpAdapterPolicy = AcpAdapterPolicy.NativeOnly };
            Assert.True(
                resolver.CanLaunchAcp(definition, installation, nativeOnly),
                $"Expected '{agentId}' ACP adapter to resolve deterministically offline under NativeOnly.");

            foreach (var policy in new[] { AcpAdapterPolicy.NativeOnly, AcpAdapterPolicy.UseLocalRunners, AcpAdapterPolicy.ManagedRegistry })
            {
                var settings = new AgentSettings { AcpAdapterPolicy = policy };
                var plan = await resolver.ResolveAsync(
                    definition,
                    installation,
                    settings,
                    paths.DataDirectory,
                    PermissionModes.Default);

                var resolvedPath = Path.GetFullPath(plan.Executable);
                Assert.StartsWith(cacheRoot, resolvedPath, StringComparison.OrdinalIgnoreCase);

                var fileName = Path.GetFileNameWithoutExtension(resolvedPath);
                Assert.NotEqual("npm", fileName, StringComparer.OrdinalIgnoreCase);
                Assert.NotEqual("npx", fileName, StringComparer.OrdinalIgnoreCase);
                Assert.NotEqual("uvx", fileName, StringComparer.OrdinalIgnoreCase);

                Assert.True(plan.Environment.TryGetValue("PATH", out var planPath));
                var pathEntries = (planPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                Assert.Contains(pathEntries, entry =>
                    Path.GetFullPath(entry).StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [AvaloniaFact]
    public async Task AppHost_acp_resolver_never_exposes_host_runners_or_public_registry_for_uncached_adapters()
    {
        var resolver = AppHost.Get<AcpLaunchResolver>();
        var paths = AppHost.Get<IAppPaths>();

        var uncachedCustom = new AgentDefinition
        {
            Id = "test-uncached-adapter",
            Name = "Uncached Test Adapter",
            Acp = new()
            {
                Mode = "adapter",
                Dialect = "generic",
                Adapter = new()
                {
                    RegistryId = "nonexistent-offline-test-adapter",
                    Executables = ["nonexistent-offline-test-adapter"],
                    NpxPackage = "@test/nonexistent-offline-test-adapter",
                    UvxPackage = "nonexistent-offline-test-adapter",
                },
            },
        };

        var useLocalRunners = new AgentSettings { AcpAdapterPolicy = AcpAdapterPolicy.UseLocalRunners };
        Assert.False(
            resolver.CanLaunchAcp(uncachedCustom, null, useLocalRunners),
            "Test AppHost must not expose host npm/npx/uvx runners to AcpLaunchResolver.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(uncachedCustom, null, useLocalRunners, paths.DataDirectory, PermissionModes.Default));

        var managedRegistry = new AgentSettings { AcpAdapterPolicy = AcpAdapterPolicy.ManagedRegistry };
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(uncachedCustom, null, managedRegistry, paths.DataDirectory, PermissionModes.Default));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Offline ManagedRegistry resolution took {sw.Elapsed}.");
    }

    [AvaloniaFact]
    public async Task TestApp_dispose_releases_active_agent_gui_tabs_and_disposes_sessions()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-teardown-").FullName;
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(tempDir, "teardown-check");
            var item = new ProjectItem
            {
                Kind = ProjectItemKind.Agent,
                AgentId = "opencode",
                Title = "OpenCode GUI",
                Surface = AgentSurface.Gui,
            };
            projects.AddItem(project, item);

            var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
            var tabs = AppHost.Get<ITabContentRegistry>();
            var view = Assert.IsType<AgentGuiTabView>(tabs.GetOrCreate(project, item));
            var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);

            await vm.ConnectAsync();
            Frames.Pump();

            var session = Assert.Single(fakeFactory.Created);
            Assert.True(session.IsConnected);

            // Simulates FreshAppAttribute.After when a test leaves an open or fire-and-forget GUI tab behind.
            TestApp.Dispose();

            Assert.False(session.IsConnected, "TestApp.Dispose() must release active tabs and dispose their ACP sessions.");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }
}
