using Octopus.Agents;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Core.Terminals;
using Octopus.Storage;
using Octopus.Terminal;

namespace Octopus.App.Tests;

/// <summary>A terminal connection that never produces output and never exits on its own: tests never start a real pty.</summary>
public sealed class FakeTerminalConnection(string? foreground = null) : ITerminalConnection
{
    private readonly TaskCompletionSource<TerminalExit> _exit = new();

    /// <summary>True once the process was ended (hangup or dispose).</summary>
    public bool IsEnded { get; private set; }
    public int ProcessId => 4242;
    public Task<TerminalExit> Exited => _exit.Task;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    /// <summary>How often the terminal changed the size of the pty.</summary>
    public int Resizes { get; private set; }
    public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        Resizes++;
        return ValueTask.CompletedTask;
    }

    public ValueTask SignalAsync(TerminalSignal signal)
    {
        IsEnded = true;
        _exit.TrySetResult(new TerminalExit(0, null));
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult(foreground);

    public ValueTask DisposeAsync()
    {
        IsEnded = true;
        _exit.TrySetResult(new TerminalExit(0, null));
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeTerminalConnectionFactory : ITerminalConnectionFactory
{
    public List<LaunchSpec> Started { get; } = [];
    public List<FakeTerminalConnection> Connections { get; } = [];

    /// <summary>The foreground program the next connections report: null means just the shell (nothing to confirm).</summary>
    public string? Foreground { get; set; }

    public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
    {
        Started.Add(spec);
        var connection = new FakeTerminalConnection(Foreground);
        Connections.Add(connection);
        return connection;
    }
}

/// <summary>A scanner that reports fixed installations instead of looking at the machine.</summary>
public sealed class FakeAgentScanner(params string[] installed) : IAgentScanner
{
    public AgentScanResult Current { get; } = new()
    {
        Installations = installed.Select(id => new AgentInstallation { AgentId = id, Path = "/usr/local/bin/" + id }).ToList(),
    };

    public bool IsScanning => false;
    public event EventHandler? Changed { add { } remove { } }
    public Task LoadCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<AgentScanResult> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    public AgentInstallation? Find(string agentId) => Current.Installations.FirstOrDefault(i => i.AgentId == agentId);
}

public sealed class FakeDialogService : IDialogService
{
    public bool Answer { get; set; } = true;
    public int Asked { get; private set; }
    public string? LastTitle { get; private set; }
    public string? LastDescription { get; private set; }
    public NewProjectChoice? NewProject { get; set; }

    public Task<NewProjectChoice?> AskNewProjectAsync() => Task.FromResult(NewProject);

    public Task<bool> ConfirmAsync(string title, string description, string confirmText, bool isDanger = false)
    {
        Asked++;
        LastTitle = title;
        LastDescription = description;
        return Task.FromResult(Answer);
    }
}

public sealed class FakePickerService : IPickerService
{
    public string? Folder { get; set; }
    public string? File { get; set; }
    public Task<string?> PickFolderAsync() => Task.FromResult(Folder);
    public Task<string?> PickFileAsync(string? startDirectory) => Task.FromResult(File);
}

/// <summary>A project store that counts its writes: the exit test checks that closing saved.</summary>
public sealed class RecordingProjectStore : IProjectStore
{
    private ProjectsDocument _document = new();

    public int Saves { get; private set; }

    public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_document);

    public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken = default)
    {
        Saves++;
        _document = document;
        return Task.CompletedTask;
    }
}
