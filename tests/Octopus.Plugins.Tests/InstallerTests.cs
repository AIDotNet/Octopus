using System.IO.Compression;
using System.Text;
using Octopus.Plugins.Packages;
using Xunit;

namespace Octopus.Plugins.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly TempAppPaths _app = new();
    private readonly PluginPaths _paths;
    private readonly PluginInstaller _installer;

    public InstallerTests()
    {
        _paths = new PluginPaths(_app);
        _installer = new PluginInstaller(_paths);
    }

    public void Dispose() => _app.Dispose();

    private string Zip(Action<ZipArchive> fill)
    {
        var file = Path.Combine(_app.Root, $"{Guid.NewGuid():N}.octoplugin");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            fill(zip);
        }
        return file;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private bool StagingIsEmpty => !Directory.Exists(_paths.StagingDirectory) || Directory.GetFileSystemEntries(_paths.StagingDirectory).Length == 0;

    [Fact]
    public void Installs_a_package_with_the_manifest_at_the_top()
    {
        var file = Zip(zip =>
        {
            Add(zip, "plugin.json", Package.Manifest("acme.csv", "\"main\": \"main.js\""));
            Add(zip, "main.js", "export function activate() {}");
        });

        var staged = _installer.Stage(file);
        Assert.True(staged.CanInstall, string.Join("\n", staged.Result.Issues));
        Assert.False(staged.IsUpgrade);

        var installed = _installer.Install(staged);
        Assert.Equal(_paths.PackageDirectoryOf("acme.csv"), installed);
        Assert.True(File.Exists(Path.Combine(installed, "main.js")));
        Assert.True(StagingIsEmpty);
    }

    [Fact]
    public void Accepts_a_package_zipped_with_its_folder_and_replaces_the_old_version()
    {
        _installer.Install(_installer.Stage(Zip(zip => Add(zip, "plugin.json", Package.Manifest("acme.csv")))));
        File.WriteAllText(Path.Combine(_paths.PackageDirectoryOf("acme.csv"), "stale.txt"), "old");

        var upgrade = _installer.Stage(Zip(zip =>
        {
            Add(zip, "acme-csv-2.0/plugin.json", Package.Manifest("acme.csv").Replace("1.0.0", "2.0.0"));
            Add(zip, "acme-csv-2.0/README.md", "new");
        }));

        Assert.True(upgrade.IsUpgrade);
        Assert.Equal("1.0.0", upgrade.InstalledVersion);
        var installed = _installer.Install(upgrade);
        Assert.True(File.Exists(Path.Combine(installed, "README.md")));
        Assert.False(File.Exists(Path.Combine(installed, "stale.txt")));
        Assert.Empty(Directory.GetDirectories(_paths.PluginsDirectory, ".backup-*"));
        Assert.True(StagingIsEmpty);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("..\\evil.txt")]
    public void Refuses_entries_that_write_outside_the_package(string name)
    {
        var file = Zip(zip =>
        {
            Add(zip, "plugin.json", Package.Manifest());
            Add(zip, name, "pwned");
        });

        Assert.Throws<PluginPackageException>(() => _installer.Stage(file));
        Assert.False(File.Exists(Path.Combine(_app.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_paths.PluginsDirectory, "evil.txt")));
        Assert.True(StagingIsEmpty);
    }

    [Fact]
    public void Refuses_links_inside_an_archive()
    {
        var file = Zip(zip =>
        {
            Add(zip, "plugin.json", Package.Manifest());
            var link = zip.CreateEntry("secret");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); // symbolic link, rwxrwxrwx
            using var stream = link.Open();
            stream.Write("/Users/someone/.ssh/id_rsa"u8);
        });

        var e = Assert.Throws<PluginPackageException>(() => _installer.Stage(file));
        Assert.Contains("link", e.Message);
    }

    [Fact]
    public void Refuses_links_inside_a_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs privileges on Windows");
        var secret = Path.Combine(_app.Root, "secret.txt");
        File.WriteAllText(secret, "token");
        var source = Package.Write(Path.Combine(_app.Root, "src-plugin"), Package.Manifest());
        File.CreateSymbolicLink(Path.Combine(source, "copied.txt"), secret);

        Assert.Throws<PluginPackageException>(() => _installer.Stage(source));
        Assert.True(StagingIsEmpty);
    }

    [Fact]
    public void Refuses_too_many_files()
    {
        var file = Zip(zip =>
        {
            Add(zip, "plugin.json", Package.Manifest());
            for (var i = 0; i < PluginInstaller.MaxEntries; i++) zip.CreateEntry($"f/{i}.txt");
        });
        Assert.Throws<PluginPackageException>(() => _installer.Stage(file));
    }

    [Fact]
    public void Stops_unpacking_past_the_size_limit_whatever_the_archive_claims()
    {
        var file = Zip(zip =>
        {
            Add(zip, "plugin.json", Package.Manifest());
            using var stream = zip.CreateEntry("zeros.bin", CompressionLevel.Optimal).Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i <= PluginInstaller.MaxUnpackedBytes / chunk.Length; i++) stream.Write(chunk);
        });
        Assert.True(new FileInfo(file).Length < 1024 * 1024, "the archive itself is small");

        var e = Assert.Throws<PluginPackageException>(() => _installer.Stage(file));
        Assert.Contains("MB", e.Message);
        Assert.True(StagingIsEmpty);
    }

    [Fact]
    public void A_package_without_a_manifest_or_with_errors_is_not_installed()
    {
        Assert.Throws<PluginPackageException>(() => _installer.Stage(Zip(zip => Add(zip, "readme.md", "hi"))));
        Assert.Throws<PluginPackageException>(() => _installer.Stage(Path.Combine(_app.Root, "nothing-here")));

        var invalid = _installer.Stage(Zip(zip => Add(zip, "plugin.json", Package.Manifest("Bad.Id"))));
        Assert.False(invalid.CanInstall);
        Assert.Throws<InvalidOperationException>(() => _installer.Install(invalid));
        _installer.Discard(invalid);
        Assert.True(StagingIsEmpty);
    }

    [Fact]
    public void Uninstall_keeps_the_plugin_data_unless_asked()
    {
        _installer.Install(_installer.Stage(Zip(zip => Add(zip, "plugin.json", Package.Manifest("acme.csv")))));
        Directory.CreateDirectory(_paths.DataDirectoryOf("acme.csv"));

        _installer.Uninstall("acme.csv", removeData: false);
        Assert.False(Directory.Exists(_paths.PackageDirectoryOf("acme.csv")));
        Assert.True(Directory.Exists(_paths.DataDirectoryOf("acme.csv")));

        _installer.Uninstall("acme.csv", removeData: true);
        Assert.False(Directory.Exists(_paths.DataDirectoryOf("acme.csv")));
    }
}
