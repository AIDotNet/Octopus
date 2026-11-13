namespace Octopus.Agents.Tests;

public class AgentSearchPathsTests
{
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
            ["/home/u/.local/bin", "/home/u/.npm-global/bin", "/usr/local/bin", "/custom/npm/bin"],
            new() { ["npm_config_prefix"] = "/custom/npm", ["NPM_CONFIG_PREFIX"] = "/custom/npm" });

        Assert.Equal(["/custom/npm/bin", "/home/u/.local/bin", "/home/u/.npm-global/bin", "/usr/local/bin"], result);
    }

    [Fact]
    public void Node_version_manager_directories_list_the_newest_version_first()
    {
        var root = "/home/u/.nvm/versions/node";
        var result = Unix(
            [root + "/v22.9.0/bin", root + "/v22.10.0/bin", root + "/v18.20.4/bin"],
            children: new() { [root] = [root + "/v18.20.4", root + "/v22.9.0", root + "/v22.10.0"] });

        Assert.Equal([root + "/v22.10.0/bin", root + "/v22.9.0/bin", root + "/v18.20.4/bin"], result);
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
    [InlineData("~/.opencode/bin", "/home/u/.opencode/bin")]
    [InlineData("$TOOLS/bin", "/opt/tools/bin")]
    [InlineData("%TOOLS%/bin", "/opt/tools/bin")]
    [InlineData("$MISSING/bin", "$MISSING/bin")]
    [InlineData("/plain/dir", "/plain/dir")]
    public void Directories_of_a_definition_are_expanded(string input, string expected) =>
        Assert.Equal(expected, AgentSearchPaths.Expand(input, "/home/u", n => n == "TOOLS" ? "/opt/tools" : null));
}
