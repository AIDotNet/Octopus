using Octopus.Platform.Windows;

namespace Octopus.Platform.Tests;

public class WindowsLoginEnvironmentTests
{
    private static string P(params string[] parts) => Path.Combine(parts);

    [Fact]
    public void Mise_directories_include_shims_bin_newest_node_and_npm_backend_tools_in_order()
    {
        var home = @"C:\Users\u";
        var local = @"C:\Users\u\AppData\Local";
        var custom = @"D:\mise";
        var mise = P(local, "mise");
        var installs = P(mise, "installs");
        var nodeRoot = P(installs, "node");
        var claudePkg = P(installs, "npm-anthropic-ai-claude-code");

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MISE_DATA_DIR"] = custom,
        };
        var children = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [installs] = [nodeRoot, claudePkg],
            [nodeRoot] = [P(nodeRoot, "20.18.0"), P(nodeRoot, "22.9.0"), P(nodeRoot, "22.14.0")],
            [claudePkg] = [P(claudePkg, "2.1.0")],
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            P(custom, "shims"),
            P(mise, "shims"),
            P(mise, "bin"),
            P(nodeRoot, "20.18.0"),
            P(nodeRoot, "22.9.0"),
            P(nodeRoot, "22.14.0"),
            P(claudePkg, "2.1.0", "node_modules", ".bin"),
        };

        var result = WindowsLoginEnvironment.MiseDirectories(
            home,
            local,
            name => env.GetValueOrDefault(name),
            existing.Contains,
            dir => children.GetValueOrDefault(dir) ?? []);

        Assert.Equal(
        [
            P(custom, "shims"),
            P(mise, "shims"),
            P(mise, "bin"),
            P(nodeRoot, "22.14.0"),
            P(nodeRoot, "22.9.0"),
            P(nodeRoot, "20.18.0"),
            P(claudePkg, "2.1.0", "node_modules", ".bin"),
        ], result);
    }

    [Fact]
    public void Mise_directories_deduplicate_when_env_matches_default_location()
    {
        var local = @"C:\Users\u\AppData\Local";
        var mise = P(local, "mise");
        var shims = P(mise, "shims");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { shims };

        var result = WindowsLoginEnvironment.MiseDirectories(
            @"C:\Users\u",
            local,
            name => name == "MISE_DATA_DIR" ? mise : null,
            existing.Contains,
            _ => []);

        Assert.Equal([shims], result);
    }
}
