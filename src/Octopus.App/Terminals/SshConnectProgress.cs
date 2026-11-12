using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Localization;
using Octopus.Ssh;

namespace Octopus.App.Terminals;

/// <summary>Where a step of the connection stands.</summary>
public enum SshStepState
{
    Pending,
    Current,
    Done,
}

/// <summary>One step of the connection, as the terminal's connection card shows it.</summary>
public sealed partial class SshConnectStep(SshStage stage, string label) : ObservableObject
{
    public SshStage Stage { get; } = stage;

    public string Label { get; } = label;

    [ObservableProperty] public partial SshStepState State { get; set; } = SshStepState.Pending;

    public bool IsDone => State == SshStepState.Done;

    public bool IsCurrent => State == SshStepState.Current;

    public bool IsPending => State == SshStepState.Pending;

    partial void OnStateChanged(SshStepState value)
    {
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(IsPending));
    }
}

/// <summary>
/// The connection of a terminal on an SSH host, shown while it is made: the steps ssh goes through, its latest protocol
/// lines, and the failure if it cannot connect. ssh writes its debug log to a file (see <c>RemoteFiles.StartConnectLog</c>);
/// this reads the new lines of that file a few times a second.
/// </summary>
public sealed partial class SshConnectProgress : ObservableObject, IDisposable
{
    private const int RecentCount = 14;

    private readonly string _host;
    private readonly string _logPath;
    /// <summary>Fires on a thread-pool thread; each tick is posted to the UI thread, where the log is read.</summary>
    private readonly Timer _timer;
    private long _offset;
    private string _partial = "";
    private SshStage? _reached;
    private bool _disposed;

    public SshConnectProgress(string host, string logPath)
    {
        _host = host;
        _logPath = logPath;
        _timer = new Timer(_ => Dispatcher.UIThread.Post(Read), null, Timeout.Infinite, Timeout.Infinite);
        Steps.Add(new SshConnectStep(SshStage.Connecting, AppLangResources.Get(AppLangResourceKind.TerminalConnectStepConnect)));
        Steps.Add(new SshConnectStep(SshStage.Negotiating, AppLangResources.Get(AppLangResourceKind.TerminalConnectStepNegotiate)));
        Steps.Add(new SshConnectStep(SshStage.CheckingHostKey, AppLangResources.Get(AppLangResourceKind.TerminalConnectStepHostKey)));
        Steps.Add(new SshConnectStep(SshStage.Authenticating, AppLangResources.Get(AppLangResourceKind.TerminalConnectStepAuth)));
        Steps.Add(new SshConnectStep(SshStage.OpeningSession, AppLangResources.Get(AppLangResourceKind.TerminalConnectStepSession)));
    }

    public ObservableCollection<SshConnectStep> Steps { get; } = [];

    /// <summary>The latest protocol lines, oldest first.</summary>
    public ObservableCollection<string> RecentLines { get; } = [];

    /// <summary>True from the start of a run until the session is open (or the run ends).</summary>
    [ObservableProperty] public partial bool IsConnecting { get; private set; }

    /// <summary>The reason the connection failed, or null.</summary>
    [ObservableProperty] public partial string? Failure { get; private set; }

    /// <summary>The card shows while the connection is made and when it failed.</summary>
    public bool IsVisible => IsConnecting || Failure is not null;

    public string Title => Failure ?? AppLangResources.Format(AppLangResourceKind.TerminalConnecting, _host);

    public string ProtocolTitle => AppLangResources.Get(AppLangResourceKind.TerminalConnectProtocol);

    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(IsVisible));

    partial void OnFailureChanged(string? value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>A run of the terminal starts: its steps are read from the log as ssh writes them.</summary>
    public void Begin()
    {
        if (_disposed) return;
        _reached = null;
        Failure = null;
        IsConnecting = true;
        // The protocol lines of this run only: the last run's are not what the person is waiting for.
        RecentLines.Clear();
        foreach (var step in Steps) step.State = SshStepState.Pending;
        Steps[0].State = SshStepState.Current;
        StartTimer();
        Read();
    }

    /// <summary>The run ended: what the log still holds is read, and a connection that never got ready says so.</summary>
    public void Ended(int? exitCode)
    {
        StopTimer();
        Read();
        if (!IsConnecting) return;
        IsConnecting = false;
        Failure ??= AppLangResources.Format(AppLangResourceKind.TerminalConnectEnded, exitCode?.ToString() ?? "?");
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private void StartTimer() => _timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(150));

    private void StopTimer() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>Reads what ssh wrote since the last read. A log that was emptied for a new run is read from its start.</summary>
    private void Read()
    {
        if (_disposed) return;
        try
        {
            using var file = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < _offset)
            {
                _offset = 0;
                _partial = "";
            }
            var count = file.Length - _offset;
            if (count <= 0) return;
            var buffer = new byte[count];
            file.Seek(_offset, SeekOrigin.Begin);
            file.ReadExactly(buffer);
            _offset = file.Length;
            var text = _partial + Encoding.UTF8.GetString(buffer);
            var lines = text.Split('\n');
            _partial = lines[^1];
            foreach (var line in lines[..^1]) ApplyLine(line.TrimEnd('\r'));
        }
        catch (IOException)
        {
            // The log is not there yet (ssh has not started): the next read finds it.
        }
    }

    private void ApplyLine(string line)
    {
        if (line.Trim().Length == 0) return;
        RecentLines.Add(SshProtocol.Clean(line));
        while (RecentLines.Count > RecentCount) RecentLines.RemoveAt(0);
        if (!IsConnecting) return;
        if (SshProtocol.FailureOf(line) is { } failure)
        {
            Failure ??= failure;
            return;
        }
        if (SshProtocol.StageOf(line) is { } stage && (_reached is null || stage > _reached))
        {
            Advance(stage);
        }
    }

    private void Advance(SshStage stage)
    {
        _reached = stage;
        foreach (var step in Steps)
        {
            step.State = step.Stage < stage ? SshStepState.Done : step.Stage == stage ? SshStepState.Current : SshStepState.Pending;
        }
        if (stage == SshStage.Ready)
        {
            foreach (var step in Steps) step.State = SshStepState.Done;
            IsConnecting = false;
            StopTimer();
        }
    }
}
