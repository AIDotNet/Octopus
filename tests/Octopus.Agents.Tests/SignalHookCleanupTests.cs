using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Agents.Signals;

namespace Octopus.Agents.Tests;

public sealed class SignalHookCleanupTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _home;
    private readonly string _dataDir;
    private readonly string _cacheDir;
    private readonly string _throttleDir;

    public SignalHookCleanupTests()
    {
        _home = Directory.CreateDirectory(Path.Combine(_dir.Path, "home")).FullName;
        _dataDir = Path.Combine(_dir.Path, "app", "data");
        _cacheDir = Path.Combine(_dir.Path, "app", "cache");
        _throttleDir = Path.Combine(_dir.Path, "throttle");
    }

    public void Dispose() => _dir.Dispose();

    private string AgyHooksPath => Path.Combine(_home, ".gemini", "config", "hooks.json");

    private void AttachAgy()
    {
        var planner = new SignalAttachPlanner(new TestPaths(Path.Combine(_dir.Path, "app")));
        var attachment = planner.Plan(new SignalAttachRequest(
            Builtins.Get("agy"),
            "item-1",
            "/opt/octopus/octopus-signal",
            new SignalEndpoint("/tmp/octo/hub.sock", "tok"),
            "1.5.12",
            new Dictionary<string, string> { ["HOME"] = _home, ["USERPROFILE"] = _home },
            _dir.Path));
        Assert.False(attachment.IsEmpty);
    }

    [Fact]
    public void Agy_removes_octopus_entry_and_keeps_users_other_hooks()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AgyHooksPath)!);
        File.WriteAllText(AgyHooksPath, """{"lint":{"PostToolUse":[{"matcher":"run_command","hooks":[{"command":"./lint.sh"}]}]}}""");
        AttachAgy();
        Assert.True(JsonDocument.Parse(File.ReadAllText(AgyHooksPath)).RootElement.TryGetProperty("octopus", out _));

        Assert.True(SignalHookCleanup.RemoveAgyHooks(_home));

        Assert.True(File.Exists(AgyHooksPath));
        var root = JsonDocument.Parse(File.ReadAllText(AgyHooksPath)).RootElement;
        Assert.False(root.TryGetProperty("octopus", out _));
        Assert.Equal("./lint.sh", root.GetProperty("lint").GetProperty("PostToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
    }

    [Fact]
    public void Agy_deletes_hooks_file_when_octopus_was_the_only_entry()
    {
        AttachAgy();
        Assert.True(File.Exists(AgyHooksPath));

        Assert.True(SignalHookCleanup.RemoveAgyHooks(_home));
        Assert.False(File.Exists(AgyHooksPath));
        Assert.True(Directory.Exists(Path.GetDirectoryName(AgyHooksPath)!));
    }

    [Fact]
    public void Agy_leaves_files_without_octopus_or_malformed_json_untouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AgyHooksPath)!);
        const string userOnly = "{\"mine\":{\"Stop\":[]}}";
        File.WriteAllText(AgyHooksPath, userOnly);
        Assert.False(SignalHookCleanup.RemoveAgyHooks(_home));
        Assert.Equal(userOnly, File.ReadAllText(AgyHooksPath));

        File.WriteAllText(AgyHooksPath, "{ not json");
        Assert.False(SignalHookCleanup.RemoveAgyHooks(_home));
        Assert.Equal("{ not json", File.ReadAllText(AgyHooksPath));
    }

    [Fact]
    public void Agy_handles_bom_comments_and_trailing_commas()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AgyHooksPath)!);
        File.WriteAllText(AgyHooksPath, "\uFEFF{\n  // user comment\n  \"octopus\": { \"Stop\": [] },\n  \"custom\": { \"Stop\": [] },\n}\n");

        Assert.True(SignalHookCleanup.RemoveAgyHooks(_home));
        var root = JsonDocument.Parse(File.ReadAllText(AgyHooksPath)).RootElement;
        Assert.False(root.TryGetProperty("octopus", out _));
        Assert.True(root.TryGetProperty("custom", out _));
    }

    [Fact]
    public void Claude_restores_all_installed_status_lines_and_deletes_bridge_directory()
    {
        var paths = new TestPaths(Path.Combine(_dir.Path, "app"));
        var bridge = new ClaudeStatusLineBridge(paths);

        var defaultClaude = Directory.CreateDirectory(Path.Combine(_home, ".claude")).FullName;
        const string originalDefault = """{ "model": "opus", "statusLine": { "type": "command", "command": "echo mine", "padding": 2 } }""";
        File.WriteAllText(Path.Combine(defaultClaude, "settings.json"), originalDefault);

        var accountDir = Directory.CreateDirectory(Path.Combine(_home, ".rec", "claude", "accounts", "7", "config")).FullName;
        File.WriteAllText(Path.Combine(accountDir, ".claude.json"), "{}");
        File.WriteAllText(Path.Combine(accountDir, "settings.json"), """{ "theme": "dark" }""");

        bridge.Install(defaultClaude);
        bridge.Install(accountDir);
        Assert.True(bridge.IsInstalled(defaultClaude));
        Assert.True(bridge.IsInstalled(accountDir));

        var restored = SignalHookCleanup.RestoreClaudeStatusLines(_home, paths.DataDirectory);

        Assert.Equal(2, restored);
        Assert.False(bridge.IsInstalled(defaultClaude));
        Assert.False(bridge.IsInstalled(accountDir));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(originalDefault),
            JsonNode.Parse(File.ReadAllText(Path.Combine(defaultClaude, "settings.json")))));
        var accountAfter = JsonNode.Parse(File.ReadAllText(Path.Combine(accountDir, "settings.json")))!;
        Assert.Null(accountAfter["statusLine"]);
        Assert.Equal("dark", accountAfter["theme"]!.GetValue<string>());
        Assert.False(Directory.Exists(bridge.Directory));
    }

    [Fact]
    public void Claude_restores_even_when_index_or_backup_is_missing_or_corrupt()
    {
        var paths = new TestPaths(Path.Combine(_dir.Path, "app"));
        var bridge = new ClaudeStatusLineBridge(paths);

        var defaultClaude = Directory.CreateDirectory(Path.Combine(_home, ".claude")).FullName;
        File.WriteAllText(Path.Combine(defaultClaude, "settings.json"), """{ "model": "sonnet" }""");
        bridge.Install(defaultClaude);

        // Simulate missing directories.json and corrupted backup file.
        File.Delete(Path.Combine(bridge.Directory, "directories.json"));
        File.WriteAllText(Path.Combine(bridge.Directory, $"{ClaudeStatusLineBridge.Hash(defaultClaude)}.statusline-backup.json"), "{ bad json");

        var restored = SignalHookCleanup.RestoreClaudeStatusLines(_home, paths.DataDirectory);

        Assert.Equal(1, restored);
        Assert.False(bridge.IsInstalled(defaultClaude));
        var after = JsonNode.Parse(File.ReadAllText(Path.Combine(defaultClaude, "settings.json")))!;
        Assert.Null(after["statusLine"]);
        Assert.Equal("sonnet", after["model"]!.GetValue<string>());
        Assert.False(Directory.Exists(bridge.Directory));
    }

    [Fact]
    public void CleanAll_removes_generated_signal_caches_and_hooks_together()
    {
        AttachAgy();
        var attachDir = Directory.CreateDirectory(Path.Combine(_cacheDir, SignalAttachPlanner.AttachDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(attachDir, "claude-settings.json"), "{}");
        var hubDir = Directory.CreateDirectory(Path.Combine(_cacheDir, SignalHub.RootDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(hubDir, "owner"), "123");
        Directory.CreateDirectory(_throttleDir);
        File.WriteAllText(Path.Combine(_throttleDir, "entry"), "1 hash");

        var result = SignalHookCleanup.CleanAll(_home, _dataDir, _cacheDir, throttleDirectory: _throttleDir);

        Assert.True(result.AgyHooksRemoved);
        Assert.True(result.SignalAttachCacheRemoved);
        Assert.True(result.SignalHubCacheRemoved);
        Assert.True(result.SignalThrottleRemoved);
        Assert.True(result.AnyCleaned);
        Assert.False(File.Exists(AgyHooksPath));
        Assert.False(Directory.Exists(attachDir));
        Assert.False(Directory.Exists(hubDir));
        Assert.False(Directory.Exists(_throttleDir));
    }

    [Fact]
    public void CleanAll_is_noop_when_nothing_exists_and_creates_no_directories()
    {
        var result = SignalHookCleanup.CleanAll(_home, _dataDir, _cacheDir, throttleDirectory: _throttleDir);

        Assert.False(result.AnyCleaned);
        Assert.False(Directory.Exists(_dataDir));
        Assert.False(Directory.Exists(_cacheDir));
        Assert.False(Directory.Exists(_throttleDir));
    }
}
