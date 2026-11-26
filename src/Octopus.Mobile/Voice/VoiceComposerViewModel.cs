using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;

namespace Octopus.Mobile.Voice;

/// <summary>Where the finger is while holding "hold to talk": over the bar, or dragged up onto one of the two targets.</summary>
public enum VoiceZone
{
    /// <summary>Releasing puts the text into the prompt field for a look before sending.</summary>
    Fill,
    Cancel,
    /// <summary>Releasing sends the text as the next prompt straight away.</summary>
    Send,
}

/// <summary>
/// "Hold to talk" of the conversation composer (design/ui/移动端输入框重写与语音输入方案.html, scheme B): the
/// microphone / keyboard switch, the bar to hold, and the overlay with the live text, the "cancel" and "send" targets,
/// and what releasing will do. The page reports the finger (<see cref="Press"/>, <see cref="Move"/>,
/// <see cref="Release"/>, <see cref="Abort"/>); everything else is here.
/// </summary>
public sealed partial class VoiceComposerViewModel : ObservableObject, IDisposable
{
    /// <summary>A dictation stops by itself after this long, as if the finger were lifted.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(20);

    private readonly VoiceService _voice;
    private readonly Action<string> _fill;
    private readonly Func<string, Task> _send;
    private readonly Action _openSettings;
    private readonly Action<string> _notice;
    private readonly Func<IReadOnlyList<string>> _hotwords;
    private IDictationSession? _session;
    private CancellationTokenSource? _start;
    private DispatcherTimer? _clock;
    private DateTime _startedAt;
    private bool _held;
    private VoiceZone _releasedIn;
    private int _generation;
    private bool _disposed;

    /// <param name="fill">Puts dictated text into the prompt field.</param>
    /// <param name="send">Sends dictated text as a prompt right away.</param>
    /// <param name="openSettings">Opens Me › Voice input.</param>
    /// <param name="notice">Shows a short message (the page's toast).</param>
    /// <param name="hotwords">Words of the conversation the engine should expect (file names, class names).</param>
    public VoiceComposerViewModel(VoiceService voice, Action<string> fill, Func<string, Task> send, Action openSettings, Action<string> notice,
        Func<IReadOnlyList<string>>? hotwords = null)
    {
        _hotwords = hotwords ?? (() => []);
        _voice = voice;
        _fill = fill;
        _send = send;
        _openSettings = openSettings;
        _notice = notice;
        _voice.Changed += OnSettingsChanged;
    }

    /// <summary>Some engine could work here: the microphone button shows.</summary>
    public bool IsSupported => _voice.IsSupported;

    /// <summary>The bar "hold to talk" is shown instead of the prompt field.</summary>
    [ObservableProperty] public partial bool IsVoiceMode { get; set; }

    /// <summary>The overlay is up: from the press until the text has been put in or sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoldText))]
    public partial bool IsRecording { get; private set; }

    /// <summary>The finger is lifted and the engine is settling the last words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(MetaText), nameof(ReleaseText))]
    public partial bool IsFinishing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCancelZone), nameof(IsSendZone), nameof(ReleaseText))]
    public partial VoiceZone Zone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(Placeholder))]
    public partial string SettledText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(Placeholder))]
    public partial string TailText { get; private set; } = "";

    /// <summary>How loud it is, 0 to 1, for the waveform.</summary>
    [ObservableProperty] public partial double Level { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial string ElapsedText { get; private set; } = "0:00";

    /// <summary>Close to <see cref="MaxDuration"/>: the time turns into a warning.</summary>
    [ObservableProperty] public partial bool IsNearLimit { get; private set; }

    /// <summary>Why the microphone cannot be used, shown above the composer with a way to the settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? ProblemText { get; private set; }

    public bool HasProblem => !string.IsNullOrEmpty(ProblemText);
    public bool IsCancelZone => Zone == VoiceZone.Cancel;
    public bool IsSendZone => Zone == VoiceZone.Send;
    public bool HasText => SettledText.Length > 0 || TailText.Length > 0;

    /// <summary>In the bubble until the first words come (or for engines that only answer after the finger is lifted).</summary>
    public string Placeholder => HasText ? "" : Text(IsFinishing ? MobileLangResourceKind.VoiceFinishing : MobileLangResourceKind.VoiceSpeakNow);

    public string HoldText => Text(IsRecording ? MobileLangResourceKind.VoiceReleaseToEnd : MobileLangResourceKind.VoiceHoldToTalk);
    public string StatusText => Text(IsFinishing ? MobileLangResourceKind.VoiceFinishing : MobileLangResourceKind.VoiceListening);
    public string MetaText => $"{StatusText} · {VoiceService.NameOf(_voice.Settings.Engine)} · {ElapsedText}";

    public string ReleaseText => IsFinishing
        ? Text(MobileLangResourceKind.VoiceFinishing)
        : Text(Zone switch
        {
            VoiceZone.Cancel => MobileLangResourceKind.VoiceReleaseCancel,
            VoiceZone.Send => MobileLangResourceKind.VoiceReleaseSend,
            _ => MobileLangResourceKind.VoiceReleaseFill,
        });

    [RelayCommand]
    private void ToggleVoiceMode()
    {
        if (IsRecording) return;
        ProblemText = null;
        IsVoiceMode = !IsVoiceMode;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        ProblemText = null;
        _openSettings();
    }

    [RelayCommand]
    private void DismissProblem() => ProblemText = null;

    /// <summary>The finger is down on "hold to talk": the overlay comes up at once and the engine starts.</summary>
    public async void Press()
    {
        if (_disposed || IsRecording) return;
        ProblemText = null;
        var problem = _voice.Check();
        if (problem != DictationProblem.None)
        {
            ProblemText = ProblemOf(problem);
            Haptic(HapticKind.Error);
            return;
        }

        var generation = ++_generation;
        _held = true;
        _releasedIn = VoiceZone.Fill;
        Zone = VoiceZone.Fill;
        SettledText = TailText = "";
        Level = 0;
        IsFinishing = false;
        IsRecording = true;
        _startedAt = DateTime.UtcNow;
        UpdateClock();
        StartClock();
        Haptic(HapticKind.Medium);

        var start = new CancellationTokenSource();
        _start = start;
        IDictationSession session;
        try
        {
            session = await _voice.StartAsync(_hotwords(), start.Token);
        }
        catch (DictationException e)
        {
            if (generation == _generation) Fail(e);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_start, start)) _start = null;
            start.Dispose();
        }

        if (generation != _generation || _disposed)
        {
            // Aborted while it was starting.
            session.Cancel();
            await session.DisposeAsync();
            return;
        }
        _session = session;
        session.LevelChanged += level => Post(generation, () => Level = level);
        session.TextChanged += (settled, tail) => Post(generation, () =>
        {
            SettledText = settled;
            TailText = tail;
        });
        session.Failed += error => Post(generation, () => Fail(error));
        // Released (or past the limit) while the engine was still starting: finish now.
        if (!_held) await FinishAsync(generation, _releasedIn);
    }

    /// <summary>The finger moved: over the bar (<see cref="VoiceZone.Fill"/>) or onto a target.</summary>
    public void Move(VoiceZone zone)
    {
        if (!IsRecording || IsFinishing || !_held || zone == Zone) return;
        Zone = zone;
        if (zone != VoiceZone.Fill) Haptic(HapticKind.Medium);
    }

    /// <summary>The finger is lifted: what happens depends on where (<see cref="Zone"/>).</summary>
    public void Release()
    {
        if (!_held) return;
        _held = false;
        _releasedIn = Zone;
        // Still starting: Press finishes once the engine is there.
        if (_session is null) return;
        _ = FinishAsync(_generation, _releasedIn);
    }

    /// <summary>Taken away from under the finger (the page left, the app went to the background, a call came in): thrown away.</summary>
    public void Abort()
    {
        if (!IsRecording) return;
        _held = false;
        _start?.Cancel();
        var session = _session;
        Reset();
        if (session is not null) _ = DropAsync(session);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Abort();
        _disposed = true;
        _voice.Changed -= OnSettingsChanged;
    }

    private async Task FinishAsync(int generation, VoiceZone zone)
    {
        if (generation != _generation || _session is not { } session) return;
        StopClock();
        if (zone == VoiceZone.Cancel)
        {
            Reset();
            await DropAsync(session);
            Haptic(HapticKind.Light);
            return;
        }

        IsFinishing = true;
        Zone = zone;
        // From here on this method owns the session and disposes of it.
        _session = null;
        string text;
        try
        {
            using var timeout = new CancellationTokenSource(FinishTimeout);
            text = await session.StopAsync(timeout.Token);
        }
        catch (DictationException e)
        {
            if (generation == _generation) Fail(e);
            return;
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) Fail(new DictationException(DictationErrorKind.Network, Text(MobileLangResourceKind.VoiceErrorTimeout)));
            return;
        }
        finally
        {
            await session.DisposeAsync();
        }

        if (generation != _generation) return;
        Reset();
        if (string.IsNullOrWhiteSpace(text))
        {
            _notice(Text(MobileLangResourceKind.VoiceErrorNoSpeech));
            return;
        }
        Haptic(HapticKind.Light);
        if (zone == VoiceZone.Send)
        {
            await _send(text);
            return;
        }
        _fill(text);
        // Back to the keyboard so the text can be checked and edited before it goes.
        IsVoiceMode = false;
    }

    private void Fail(DictationException e)
    {
        var session = _session;
        Reset();
        if (session is not null) _ = DropAsync(session);
        Haptic(HapticKind.Error);
        var message = ErrorOf(e);
        // What needs the person to change something stays above the composer with the way to the settings.
        var lasting = e.Kind is DictationErrorKind.PermissionDenied or DictationErrorKind.Auth or DictationErrorKind.Unavailable
            or DictationErrorKind.NotSignedIn or DictationErrorKind.ServerOff or DictationErrorKind.Quota;
        if (lasting) ProblemText = message;
        else _notice(message);
    }

    private void Reset()
    {
        _generation++;
        _session = null;
        _held = false;
        StopClock();
        IsRecording = false;
        IsFinishing = false;
        Zone = VoiceZone.Fill;
        Level = 0;
        IsNearLimit = false;
        SettledText = TailText = "";
    }

    private static async Task DropAsync(IDictationSession session)
    {
        session.Cancel();
        try
        {
            await session.DisposeAsync();
        }
        catch (DictationException)
        {
        }
    }

    private void StartClock()
    {
        if (Application.Current is null) return;
        _clock ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => OnClockTick());
        _clock.Start();
    }

    private void StopClock() => _clock?.Stop();

    private void OnClockTick()
    {
        if (!IsRecording || IsFinishing) return;
        UpdateClock();
        if (DateTime.UtcNow - _startedAt < MaxDuration || !_held) return;
        // At the limit: as if the finger were lifted where it is now.
        Haptic(HapticKind.Light);
        Release();
    }

    private void UpdateClock()
    {
        var elapsed = DateTime.UtcNow - _startedAt;
        if (elapsed > MaxDuration) elapsed = MaxDuration;
        ElapsedText = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        IsNearLimit = elapsed >= WarnAfter;
    }

    private void Post(int generation, Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (generation == _generation) action();
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _generation) action();
        });
    }

    private void OnSettingsChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(MetaText));
        if (_voice.Check() == DictationProblem.None) ProblemText = null;
    });

    private void Haptic(HapticKind kind)
    {
        if (_voice.Settings.Haptics) MobileHost.Haptic(kind);
    }

    private string ProblemOf(DictationProblem problem) => problem switch
    {
        DictationProblem.NoSystemRecognizer => Text(MobileLangResourceKind.VoiceProblemNoSystem),
        DictationProblem.NotConfigured => string.Format(Text(MobileLangResourceKind.VoiceProblemNotConfigured), VoiceService.NameOf(_voice.Settings.Engine)),
        DictationProblem.NotSignedIn => Text(MobileLangResourceKind.VoiceProblemNotSignedIn),
        _ => Text(MobileLangResourceKind.VoiceProblemNoMicrophone),
    };

    private string ErrorOf(DictationException e) => e.Kind switch
    {
        DictationErrorKind.PermissionDenied => Text(MobileLangResourceKind.VoiceErrorPermission),
        DictationErrorKind.Auth => string.Format(Text(MobileLangResourceKind.VoiceErrorAuth), VoiceService.NameOf(_voice.Settings.Engine)),
        DictationErrorKind.Unavailable => Text(MobileLangResourceKind.VoiceProblemNoSystem),
        DictationErrorKind.NoSpeech => Text(MobileLangResourceKind.VoiceErrorNoSpeech),
        DictationErrorKind.NotSignedIn => Text(MobileLangResourceKind.VoiceProblemNotSignedIn),
        DictationErrorKind.ServerOff => Text(MobileLangResourceKind.VoiceErrorServerOff),
        DictationErrorKind.Quota => Text(MobileLangResourceKind.VoiceErrorQuota),
        DictationErrorKind.Network => string.Format(Text(MobileLangResourceKind.VoiceErrorNetwork), e.Message),
        _ => $"{VoiceService.NameOf(_voice.Settings.Engine)}: {e.Message}",
    };

    private static string Text(MobileLangResourceKind kind) => MobileLangResources.Get(kind);
}
