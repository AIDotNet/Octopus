using System.Text.Json;
using Octopus.Agents;
using Octopus.App.Composition;
using Octopus.App.Settings.Agents;
using Octopus.Core.Agents;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.App.Tests.Settings;

/// <summary>Builds agent rows from the real catalog, compiler and in-memory settings of the test host.</summary>
internal static class SettingsTestSupport
{
    /// <summary>The test host's services live across tests, so every test starts from default settings.</summary>
    public static void Reset() => AppHost.Get<ISettingsStore>().Update(s =>
    {
        s.Agents = new AgentSettings();
        s.Terminal = new TerminalSettings();
        s.Appearance = new AppearanceSettings();
    });

    public static AgentServices Services() => new(
        AppHost.Get<ISettingsStore>(),
        AppHost.Get<IAgentCommandCompiler>(),
        AppHost.Get<IModelDiscovery>(),
        AppHost.Get<IFileOpener>(),
        new FakePathPicker());

    public static AgentDefinition Definition(string id) => AppHost.Get<IAgentCatalog>().Find(id)!;

    /// <summary>A row for a built-in agent, as if it was found at <c>/usr/local/bin/&lt;id&gt;</c>.</summary>
    public static AgentRowViewModel Row(string id, AgentDefinition? definition = null) =>
        new(definition ?? Definition(id), new AgentInstallation { AgentId = id, Path = "/usr/local/bin/" + id, Version = "1.0.0" }, Services());

    public static AgentDefinition Clone(AgentDefinition definition) =>
        JsonSerializer.Deserialize<AgentDefinition>(JsonSerializer.Serialize(definition))!;

    public static T Field<T>(this AgentRowViewModel row, string key) where T : AgentFieldViewModel =>
        (T)row.Fields.Single(f => f.Key == key);

    public static IReadOnlyList<string> Keys(this AgentRowViewModel row) => [.. row.Fields.Select(f => f.Key)];

    private sealed class FakePathPicker : IPathPicker
    {
        public Task<string?> PickAsync(bool folder, string title) => Task.FromResult<string?>(null);
    }

    /// <summary>A scanner that has "found" the given agents.</summary>
    public sealed class FakeScanner(params string[] installed) : IAgentScanner
    {
        public AgentScanResult Current { get; } = new()
        {
            ScannedAt = new DateTimeOffset(2026, 11, 11, 10, 30, 0, TimeSpan.Zero),
            Installations = [.. installed.Select(id => new AgentInstallation
            {
                AgentId = id,
                Path = id == "claude" ? "/Users/me/.npm-global/bin/claude" : id == "cursor" ? "/usr/local/bin/cursor-agent" : "/usr/local/bin/" + id,
                Version = id switch { "claude" => "2.1.322", "codex" => "0.58.0", "opencode" => "1.2.3", "cursor" => "2026.11.01", _ => "1.0.0" }
            })]
        };

        public bool IsScanning => false;
        public event EventHandler? Changed { add { } remove { } }
        public Task LoadCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public AgentInstallation? Find(string agentId) => Current.Installations.FirstOrDefault(i => i.AgentId == agentId);
    }
}
