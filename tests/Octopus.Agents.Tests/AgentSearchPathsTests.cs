namespace Octopus.Agents.Tests;

public class AgentSearchPathsTests
{
    // The Unix layout is computed on every OS, so expected paths are built with Path.Combine to use the host separator.
    private static string P(params string[] parts) => Path.Combine(parts);

    private static IReadOnlyList<string> Unix(IEnumerable<string> existing, Dictionary<string, string>? env = null, Dictionary<string, string[]>? children = null)
    {
        var set = existing.ToHashSet();
        env ??= [];
        children ??= [];
        return AgentSearchPaths.Compute(
            "/home/u", false,
            name => env.GetValueOrDefault(name),
            set.Contains,
            dir => children.GetValueOrDefault(dir) ?? []);
    }

    [Fact]
    public void Only_existing_directories_are_listed_once_in_priority_order()
    {
        var result = Unix(
            [P("/home/u", ".local", "bin"), P("/home/u", ".npm-global", "bin"), "/usr/local/bin", P("/custom/npm", "bin")],
            new() { ["npm_config_prefix"] = "/custom/npm", ["NPM_CONFIG_PREFIX"] = "/custom/npm" });

        Assert.Equal([P("/custom/npm", "bin"), P("/home/u", ".local", "bin"), P("/home/u", ".npm-global", "bin"), "/usr/local/bin"], result);
    }

    [Fact]
    public void Node_version_manager_directories_list_the_newest_version_first()
    {
        var root = P("/home/u", ".nvm", "versions", "node");
        var result = Unix(
            [P(root, "v22.9.0", "bin"), P(root, "v22.10.0", "bin"), P(root, "v18.20.4", "bin")],
            children: new() { [root] = [P(root, "v18.20.4"), P(root, "v22.9.0"), P(root, "v22.10.0")] });

        Assert.Equal([P(root, "v22.10.0", "bin"), P(root, "v22.9.0", "bin"), P(root, "v18.20.4", "bin")], result);
    }

    [Fact]
    public void Windows_uses_the_user_profile_layout_and_ignores_case()
    {
        var env = new Dictionary<string, string> { ["APPDATA"] = @"C:\Users\u\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\Users\u\AppData\Roaming", "npm"),
            Path.Combine(@"C:\Users\u", ".local", "bin"),
            Path.Combine(@"C:\Users\u", "scoop", "shims"),
        };

        var result = AgentSearchPaths.Compute(@"C:\Users\u", true, env.GetValueOrDefault, existing.Contains, _ => []);

        Assert.Equal(3, result.Count);
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Roaming", "npm"), result[0]);
    }

    [Fact]
    public void Nothing_is_listed_without_a_home_directory() =>
        Assert.Empty(AgentSearchPaths.Compute("", false, _ => null, _ => true, _ => []));

    [Theory]
    [InlineData("~", "/home/u")]
    [InlineData("$TOOLS/bin", "/opt/tools/bin")]
    [InlineData("%TOOLS%/bin", "/opt/tools/bin")]
    [InlineData("$MISSING/bin", "$MISSING/bin")]
    [InlineData("/plain/dir", "/plain/dir")]
    public void Directories_of_a_definition_are_expanded(string input, string expected) =>
        Assert.Equal(expected, AgentSearchPaths.Expand(input, "/home/u", n => n == "TOOLS" ? "/opt/tools" : null));

    [Fact]
    public void A_tilde_directory_is_expanded_to_the_home_directory() =>
        Assert.Equal(P("/home/u", ".opencode", "bin"), AgentSearchPaths.Expand("~/.opencode/bin", "/home/u", _ => null));
}
