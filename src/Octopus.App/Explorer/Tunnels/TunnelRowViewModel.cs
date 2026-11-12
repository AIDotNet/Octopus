using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Projects;
using Octopus.Ssh;

namespace Octopus.App.Explorer.Tunnels;

/// <summary>One tunnel in the panel: its name and route, the switch that runs it, and what is wrong with it, if anything.</summary>
public sealed partial class TunnelRowViewModel : ObservableObject, IDisposable
{
    private readonly TunnelsPanelViewModel _panel;
    private readonly TunnelRun _run;
    private readonly UiContext _ui = new();
    private bool _disposed;

    internal TunnelRowViewModel(TunnelSpec spec, TunnelRun run, TunnelsPanelViewModel panel)
    {
        Spec = spec;
        _run = run;
        _panel = panel;
        run.Changed += OnRunChanged;
    }

    public TunnelSpec Spec { get; }

    public string Id => Spec.Id;

    public TunnelStatus Status => _run.Status;

    /// <summary>The name the person gave, or one made from where the tunnel leads.</summary>
    public string Title => string.IsNullOrWhiteSpace(Spec.Name)
        ? AppLangResources.Format(Spec.Kind == TunnelKind.Local ? AppLangResourceKind.TunnelsNameLocal : AppLangResourceKind.TunnelsNameRemote, Target)
        : Spec.Name.Trim();

    /// <summary>Which way connections go, with the places named: "host :5173 → this computer :5173".</summary>
    public string Route
    {
        get
        {
            var route = AppLangResources.Format(
                Spec.Kind == TunnelKind.Local ? AppLangResourceKind.TunnelsRouteLocal : AppLangResourceKind.TunnelsRouteRemote,
                Target,
                Spec.ListenPort);
            return SshTunnelCommand.IsLoopback(Spec.BindAddress) ? route : route + "  ·  " + Spec.BindAddress;
        }
    }

    /// <summary>The target as "host:port"; the host itself and this computer are left out, as ":5173".</summary>
    private string Target => Spec.TargetHost is "localhost" or "127.0.0.1" ? ":" + Spec.TargetPort : Spec.TargetHost + ":" + Spec.TargetPort;

    /// <summary>The switch: on while the tunnel is being connected, runs, or is being connected again.</summary>
    public bool IsOn
    {
        get => Status.IsActive;
        set
        {
            if (value == Status.IsActive) return;
            _panel.SetRunning(this, value);
            NotifyStatus();
        }
    }

    public bool IsRunning => Status.State == TunnelState.Running;
    public bool IsBusy => Status.State is TunnelState.Connecting or TunnelState.Reconnecting or TunnelState.Waiting;
    public bool IsFailed => Status.State == TunnelState.Failed;

    /// <summary>The line under the route: what is going on when it is not simply running or stopped.</summary>
    public string? Message
    {
        get
        {
            var status = Status;
            return status.State switch
            {
                TunnelState.Connecting => AppLangResources.Get(AppLangResourceKind.TunnelsStatusConnecting),
                TunnelState.Reconnecting => AppLangResources.Format(AppLangResourceKind.TunnelsStatusReconnecting, status.Attempt) + " " + FailureText(status),
                TunnelState.Waiting => AppLangResources.Format(AppLangResourceKind.TunnelsStatusWaiting, _panel.Host ?? ""),
                TunnelState.Failed => FailureText(status),
                TunnelState.Running when status.Warning == TunnelWarning.TargetUnreachable => AppLangResources.Get(AppLangResourceKind.TunnelsWarnTarget),
                TunnelState.Running when status.Warning == TunnelWarning.ForwardingProhibited => AppLangResources.Get(AppLangResourceKind.TunnelsWarnProhibited),
                TunnelState.Running when status.Shared => AppLangResources.Get(AppLangResourceKind.TunnelsStatusShared),
                _ => null,
            };
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary>A problem rather than progress: shown in the danger color.</summary>
    public bool IsProblem => IsFailed;

    /// <summary>A connection through a running tunnel failed.</summary>
    public bool IsWarning => IsRunning && Status.Warning != TunnelWarning.None;

    /// <summary>ssh's own words behind the message, for the tooltip.</summary>
    public string? Detail => Status.Detail;

    /// <summary>The address to use the tunnel at, as the side that listens sees it.</summary>
    public string Address
    {
        get
        {
            var host = Spec.Kind == TunnelKind.Local && Spec.BindAddress is "0.0.0.0" or "*" or "127.0.0.1" or "[::]" ? "localhost" : Spec.BindAddress;
            return Spec.Kind == TunnelKind.Local ? $"{host}:{Spec.ListenPort}" : $"localhost:{Spec.ListenPort}";
        }
    }

    /// <summary>Only a local tunnel leads to something that can be opened here.</summary>
    public bool CanOpen => Spec.Kind == TunnelKind.Local;

    [ObservableProperty] public partial bool IsLogOpen { get; private set; }

    public string LogText
    {
        get
        {
            var lines = _run.Log();
            return lines.Count == 0 ? AppLangResources.Get(AppLangResourceKind.TunnelsLogEmpty) : string.Join('\n', lines);
        }
    }

    partial void OnIsLogOpenChanged(bool value) => OnPropertyChanged(nameof(LogText));

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    private void Open() => _panel.Open(this);

    [RelayCommand]
    private Task CopyAddressAsync() => _panel.CopyAddressAsync(this);

    [RelayCommand]
    private Task EditAsync() => _panel.EditAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => _panel.DeleteAsync(this);

    private void OnRunChanged(object? sender, EventArgs e)
    {
        // Called on a background thread by the service.
        _ui.Post(() =>
        {
            if (!_disposed) NotifyStatus();
        });
    }

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(Detail));
        if (IsLogOpen) OnPropertyChanged(nameof(LogText));
    }

    private string FailureText(TunnelStatus status) => status.Failure switch
    {
        TunnelFailure.InvalidSetup => AppLangResources.Get(AppLangResourceKind.TunnelsFailInvalid),
        TunnelFailure.LocalPortInUse => AppLangResources.Format(AppLangResourceKind.TunnelsFailLocalPort, Spec.ListenPort),
        TunnelFailure.PortUsedByTunnel => AppLangResources.Format(AppLangResourceKind.TunnelsFailPortTunnel, Spec.ListenPort),
        TunnelFailure.RemoteForwardRefused => AppLangResources.Format(AppLangResourceKind.TunnelsFailRemoteForward, Spec.ListenPort),
        TunnelFailure.LoginDenied => AppLangResources.Get(AppLangResourceKind.TunnelsFailLogin),
        TunnelFailure.HostKeyRejected => AppLangResources.Get(AppLangResourceKind.TunnelsFailHostKey),
        TunnelFailure.ConnectionLost => AppLangResources.Get(AppLangResourceKind.TunnelsFailLost),
        TunnelFailure.TimedOut => AppLangResources.Get(AppLangResourceKind.TunnelsFailTimeout),
        TunnelFailure.StartFailed => AppLangResources.Format(AppLangResourceKind.TunnelsFailStart, status.Detail ?? ""),
        TunnelFailure.Exited => AppLangResources.Get(AppLangResourceKind.TunnelsFailExited),
        TunnelFailure.SharedForwardFailed => AppLangResources.Format(AppLangResourceKind.TunnelsFailShared, status.Detail ?? ""),
        _ => "",
    };

    public void Dispose()
    {
        _disposed = true;
        _run.Changed -= OnRunChanged;
    }
}
