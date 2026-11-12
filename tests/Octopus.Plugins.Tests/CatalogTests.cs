using Octopus.Core.Plugins;
using Octopus.Plugins.Catalog;
using Xunit;

namespace Octopus.Plugins.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly TempAppPaths _app = new();
    private readonly MemoryPluginState _state = new();
    private readonly string _builtin;
    private readonly string _dev;
    private readonly PluginPaths _paths;

    public CatalogTests()
    {
        _paths = new PluginPaths(_app);
        _builtin = Path.Combine(_app.Root, "builtin");
        _dev = Path.Combine(_app.Root, "dev");
    }

    public void Dispose() => _app.Dispose();

    private PluginCatalog Catalog(string appVersion = "1.0.0", params string[] devDirectories)
    {
        SemanticVersion.TryParse(appVersion, out var version);
        var catalog = new PluginCatalog(_paths, _state, PluginHostInfo.Current(version),
            new PluginCatalogOptions { BuiltinRoots = [_builtin], DevDirectories = () => devDirectories });
        catalog.Refresh();
        return catalog;
    }

    [Fact]
    public void Builtin_plugins_start_enabled_and_installed_ones_start_disabled()
    {
        Package.Write(Path.Combine(_builtin, "octopus.usage"), Package.Manifest("octopus.usage"));
        Package.Write(_paths.PackageDirectoryOf("acme.csv"), Package.Manifest("acme.csv"));

        var catalog = Catalog();

        Assert.Equal(PluginStatus.Enabled, catalog.Find("octopus.usage")!.Status);
        Assert.Equal(PluginStatus.Disabled, catalog.Find("acme.csv")!.Status);
    }

    [Fact]
    public void The_state_file_decides_once_it_has_a_record()
    {
        Package.Write(Path.Combine(_builtin, "octopus.usage"), Package.Manifest("octopus.usage"));
        Package.Write(_paths.PackageDirectoryOf("acme.csv"), Package.Manifest("acme.csv"));
        _state.Update(d =>
        {
            d.Plugins["octopus.usage"] = new PluginRecord { Enabled = false, Source = PluginSource.Builtin };
            d.Plugins["acme.csv"] = new PluginRecord { Enabled = true, Source = PluginSource.File };
        });

        var catalog = Catalog();

        Assert.Equal(PluginStatus.Disabled, catalog.Find("octopus.usage")!.Status);
        Assert.Equal(PluginStatus.Enabled, catalog.Find("acme.csv")!.Status);
    }

    [Fact]
    public void Broken_and_misplaced_packages_are_invalid_not_dropped()
    {
        Package.Write(_paths.PackageDirectoryOf("acme.broken"), "{ not json");
        Package.Write(_paths.PackageDirectoryOf("acme.misplaced"), Package.Manifest("acme.other"));
        Directory.CreateDirectory(_paths.PackageDirectoryOf("acme.empty"));

        var catalog = Catalog();

        Assert.All(catalog.Packages, p => Assert.Equal(PluginStatus.Invalid, p.Status));
        Assert.Equal(["acme.broken", "acme.empty", "acme.misplaced"], catalog.Packages.Select(p => p.Id).Order());
        Assert.Contains(catalog.Find("acme.empty")!.Issues, i => i.Message.Contains("plugin.json not found"));
    }

    [Fact]
    public void Plugins_for_a_newer_api_or_another_app_version_are_incompatible()
    {
        Package.Write(_paths.PackageDirectoryOf("acme.future"), Package.Manifest("acme.future", api: PluginApi.CurrentVersion + 1));
        Package.Write(_paths.PackageDirectoryOf("acme.ranged"), Package.Manifest("acme.ranged", "\"engines\": { \"api\": 0, \"octopus\": \">=2.0.0\" }")
            .Replace("\"engines\": { \"api\": 0 }, ", ""));

        var catalog = Catalog("1.5.0");

        Assert.Equal(PluginStatus.Incompatible, catalog.Find("acme.future")!.Status);
        Assert.Contains(catalog.Find("acme.future")!.Issues, i => i.Message.Contains("Update Octopus"));
        Assert.Equal(PluginStatus.Incompatible, catalog.Find("acme.ranged")!.Status);
        Assert.Equal(PluginStatus.Disabled, Catalog("2.1.0").Find("acme.ranged")!.Status);
    }

    [Fact]
    public void Development_beats_installed_beats_builtin()
    {
        Package.Write(Path.Combine(_builtin, "acme.tool"), Package.Manifest("acme.tool"));
        Package.Write(_paths.PackageDirectoryOf("acme.tool"), Package.Manifest("acme.tool"));
        var devCopy = Package.Write(Path.Combine(_dev, "my-checkout"), Package.Manifest("acme.tool"));

        var catalog = Catalog("1.0.0", devCopy);

        var winner = catalog.Find("acme.tool")!;
        Assert.Equal(PluginSource.Dev, winner.Source);
        Assert.Equal(2, catalog.Packages.Count(p => p.Id == "acme.tool" && p.Status == PluginStatus.Shadowed));
        Assert.Equal(PluginSource.File, Catalog().Find("acme.tool")!.Source);
    }

    [Fact]
    public void Staging_and_backup_directories_are_not_packages()
    {
        Package.Write(Path.Combine(_paths.StagingDirectory, "abc"), Package.Manifest("acme.half"));
        Package.Write(Path.Combine(_paths.PluginsDirectory, ".backup-acme.x-1"), Package.Manifest("acme.x"));

        Assert.Empty(Catalog().Packages);
    }
}
