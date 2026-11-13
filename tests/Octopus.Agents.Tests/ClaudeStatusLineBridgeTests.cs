using System.Diagnostics;
using System.Text.Json.Nodes;
using Octopus.Agents.Signals;
using Xunit;

namespace Octopus.Agents.Tests;

/// <summary>
/// The Claude status line bridge: installing keeps everything else in settings.json and the user's own status line
/// (which still runs), the script hands Claude Code's JSON to Octopus, and restoring puts the file back exactly.
/// </summary>
public sealed class ClaudeStatusLineBridgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("octopus-bridge-").FullName;
    private readonly ClaudeStatusLineBridge _bridge;
    private readonly string _config;

    public ClaudeStatusLineBridgeTests()
    {
        _bridge = new ClaudeStatusLineBridge(new TestPaths(Path.Combine(_root, "octopus")));
        _config = Directory.CreateDirectory(Path.Combine(_root, "home", ".rec", "claude", "accounts", "7", "config")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Settings => Path.Combine(_config, "settings.json");

    [Fact]
    public void Install_KeepsOtherSettings_AndTheOldStatusLineOptions_AndRestorePutsItBack()
    {
        const string original = """{ "model": "opus", "statusLine": { "type": "command", "command": "echo mine", "padding": 2 }, "hooks": {} }""";
        File.WriteAllText(Settings, original);
        var before = JsonNode.Parse(original)!;

        _bridge.Install(_config);
        var installed = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.True(_bridge.IsInstalled(_config));
        Assert.Contains(ClaudeStatusLineBridge.Marker, installed["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal(2, installed["statusLine"]!["padding"]!.GetValue<int>());
        Assert.Equal("opus", installed["model"]!.GetValue<string>());
        Assert.Equal([_config], _bridge.InstalledDirectories());

        // Installing twice does not stack the bridge on itself.
        _bridge.Install(_config);
        Assert.Equal(installed.ToJsonString(), JsonNode.Parse(File.ReadAllText(Settings))!.ToJsonString());

        _bridge.Restore(_config);
        Assert.True(JsonNode.DeepEquals(before, JsonNode.Parse(File.ReadAllText(Settings))));
        Assert.False(_bridge.IsInstalled(_config));
        Assert.Empty(_bridge.InstalledDirectories());
    }

    [Fact]
    public void A_settings_file_that_cannot_be_read_is_never_changed()
    {
        File.WriteAllText(Settings, "{ not json");
        Assert.Throws<InvalidOperationException>(() => _bridge.Install(_config));
        Assert.Equal("{ not json", File.ReadAllText(Settings));
    }

    [Fact]
    public void Restore_without_an_earlier_status_line_removes_it()
    {
        _bridge.Install(_config);
        _bridge.Restore(_config);
        Assert.Null(JsonNode.Parse(File.ReadAllText(Settings))!["statusLine"]);
    }

    [Fact]
    public async Task The_script_saves_Claudes_json_and_still_runs_the_users_status_line()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The POSIX script is tested here.");
        var marker = Path.Combine(_root, "ran.txt");
        File.WriteAllText(Settings, $$"""{ "statusLine": { "type": "command", "command": "cat > '{{marker}}'; printf 'my line'" } }""");
        _bridge.Install(_config);
        const string payload = """{"rate_limits":{"five_hour":{"used_percentage":64,"resets_at":1762952400}}}""";

        var info = new ProcessStartInfo("/bin/sh", ["-c", _bridge.Command(_config)]) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        var shown = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal("my line", shown);
        Assert.Equal(payload, File.ReadAllText(marker));
        var report = _bridge.Read(_config);
        Assert.NotNull(report);
        Assert.Equal(payload, report.Value.Json);
        Assert.Equal(_config, _bridge.DirectoryOfOutput(_bridge.OutputPath(_config)));
    }

    [Fact]
    public void Discovery_finds_the_default_folder_and_account_managers_folders()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(_config, ".claude.json"), """{ "oauthAccount": { "emailAddress": "someone@example.com", "displayName": "Someone" } }""");
        Directory.CreateDirectory(Path.Combine(home, ".config", "node_modules", "x"));

        var found = ClaudeConfigDirectories.Find(home, null);

        Assert.Contains(Path.Combine(home, ".claude"), found);
        Assert.Contains(_config, found);
        Assert.Equal("Someone (someone@example.com)", ClaudeConfigDirectories.AccountOf(_config, home));
    }

    [Fact]
    public void The_watcher_registers_the_account_and_publishes_the_last_report()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The POSIX script is tested here.");
        _bridge.Install(_config);
        File.WriteAllText(_bridge.OutputPath(_config), """{"rate_limits":{}}""");
        var hub = new RecordingHub();
        using var watcher = new ClaudeStatusLineWatcher(_bridge, hub);

        watcher.Start();

        var signal = Assert.Single(hub.Published);
        Assert.Equal(("claude", "statusline", "key-1"), (signal.AgentId, signal.Channel, signal.Account));
        Assert.Equal([_config], hub.Registered.Distinct());
    }

    private sealed class RecordingHub : ISignalHub
    {
        public List<AgentSignal> Published { get; } = [];
        public List<string> Registered { get; } = [];
        public SignalEndpoint? Endpoint => null;
        public bool Start() => true;
        public IDisposable Subscribe(string? agentId, string? channel, Action<AgentSignal> listener) => new Nothing();
        public AgentSignal? LastSignal(string agentId, string channel, string? itemId = null) => null;
        public void ForgetItem(string itemId) { }
        public IReadOnlyList<AgentAccount> Accounts(string agentId) => [];
        public int AccountsVersion => 0;
        public void Publish(AgentSignal signal) => Published.Add(signal);
        public string RegisterAccount(string agentId, string directory)
        {
            Registered.Add(directory);
            return "key-1";
        }
        public SignalHubStatistics Statistics => new(0, 0);
        public void Dispose() { }
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
