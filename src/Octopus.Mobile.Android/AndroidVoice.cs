using System.Text;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Speech;
using Android.Util;
using Android.Views;
using Octopus.Mobile.Voice;

namespace Octopus.Mobile.Android;

/// <summary>Runtime permissions asked from the main activity (RECORD_AUDIO for voice input).</summary>
internal static class AndroidPermissions
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<int, (string Permission, TaskCompletionSource<bool> Asked)> Pending = [];
    private static readonly Dictionary<string, TaskCompletionSource<bool>> InFlight = new(StringComparer.Ordinal);
    private static int s_nextCode = 48100;

    public static Task<bool> EnsureAsync(string permission)
    {
        if (MainActivity.Current is not { } current || !current.TryGetTarget(out var activity)) return Task.FromResult(false);
        if (activity.CheckSelfPermission(permission) == Permission.Granted) return Task.FromResult(true);
        TaskCompletionSource<bool> asked;
        int code;
        lock (Gate)
        {
            if (InFlight.TryGetValue(permission, out var existing)) return existing.Task;
            asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            code = s_nextCode++;
            Pending[code] = (permission, asked);
            InFlight[permission] = asked;
        }
        activity.RunOnUiThread(() => activity.RequestPermissions([permission], code));
        return asked.Task;
    }

    /// <summary>From <see cref="MainActivity.OnRequestPermissionsResult"/>.</summary>
    public static void Complete(int requestCode, Permission[] results)
    {
        (string Permission, TaskCompletionSource<bool> Asked) entry;
        lock (Gate)
        {
            if (!Pending.Remove(requestCode, out entry)) return;
            InFlight.Remove(entry.Permission);
        }
        entry.Asked.TrySetResult(results.Length > 0 && results.All(result => result == Permission.Granted));
    }
}

/// <summary>Taps the phone can feel, through the window's own haptic feedback (no VIBRATE permission needed).</summary>
internal static class AndroidHaptics
{
    public static void Perform(HapticKind kind)
    {
        if (MainActivity.Current is not { } current || !current.TryGetTarget(out var activity)) return;
        var feedback = kind switch
        {
            HapticKind.Light => FeedbackConstants.KeyboardTap,
            HapticKind.Error when OperatingSystem.IsAndroidVersionAtLeast(30) => FeedbackConstants.Reject,
            _ => FeedbackConstants.LongPress,
        };
        activity.RunOnUiThread(() => activity.Window?.DecorView?.PerformHapticFeedback(feedback));
    }
}

/// <summary>
/// The phone's own recognizer (<see cref="SpeechRecognizer"/>), usually Google's. Missing on phones without Google's
/// speech service (many Chinese Android phones, HarmonyOS): then the person picks a cloud engine.
/// </summary>
internal sealed class AndroidSpeechDictation(Context context) : IDictationEngine
{
    public DictationProblem Check(VoiceSettings settings) =>
        HasSystemRecognizer(context) ? DictationProblem.None : DictationProblem.NoSystemRecognizer;

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        _ = await AndroidPermissions.EnsureAsync(Manifest.Permission.RecordAudio).ConfigureAwait(false);
    }

    public async Task<IDictationSession> StartAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        if (!await AndroidPermissions.EnsureAsync(Manifest.Permission.RecordAudio).ConfigureAwait(false))
        {
            throw new DictationException(DictationErrorKind.PermissionDenied, "RECORD_AUDIO");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var session = new AndroidSpeechSession(context, settings);
        await session.StartAsync().ConfigureAwait(false);
        return session;
    }

    internal static bool HasSystemRecognizer(Context context) =>
        SpeechRecognizer.IsRecognitionAvailable(context) ||
        (OperatingSystem.IsAndroidVersionAtLeast(31) && SpeechRecognizer.IsOnDeviceRecognitionAvailable(context));
}

/// <summary>
/// One dictation with <see cref="SpeechRecognizer"/>. The recognizer ends by itself after a pause; while the finger is
/// still down it is started again and the text runs on, so a pause to think does not end the dictation.
/// Everything with the recognizer happens on the main thread.
/// </summary>
internal sealed class AndroidSpeechSession(Context context, VoiceSettings settings) : IDictationSession
{
    private const string LogTag = "OctopusVoice";
    private const long ReadyTimeoutMs = 3500;
    private const long RestartDelayMs = 100;
    private const long RetryDelayMs = 150;
    private const int ErrorServerDisconnected = 11;
    private const int ErrorLanguageNotSupported = 12;
    private const int ErrorLanguageUnavailable = 13;
    private const int ErrorCannotCheckSupport = 14;

    private static readonly Handler Main = new(Looper.MainLooper!);
    private static readonly TimeSpan FinalTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan UnreadyStopTimeout = TimeSpan.FromMilliseconds(1200);
    private readonly StringBuilder _settled = new();
    private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DictationFailureRelay _failures = new();
    private SpeechRecognizer? _recognizer;
    private RecognitionListener? _listener;
    private string _tail = "";
    private bool _usingOnDevice;
    private bool _triedOnDevice;
    private bool _ready;
    private bool _everReady;
    private int _unreadyFailures;
    private int _listenSequence;
    private bool _stopping;
    private bool _cancelled;

    public event Action<float>? LevelChanged;
    public event Action<string, string>? TextChanged;
    public event Action<DictationException>? Failed
    {
        add => _failures.Add(value);
        remove => _failures.Remove(value);
    }

    public Task StartAsync() => OnMainAsync(() =>
    {
        var defaultAvailable = SpeechRecognizer.IsRecognitionAvailable(context);
        var onDeviceAvailable = OperatingSystem.IsAndroidVersionAtLeast(31) && SpeechRecognizer.IsOnDeviceRecognitionAvailable(context);
        var defaultService = ReadDefaultRecognitionService();
        Log.Info(LogTag, $"StartAsync: defaultAvailable={defaultAvailable}, onDeviceAvailable={onDeviceAvailable}, defaultService={defaultService ?? "(none)"}, language={ResolveLanguageTag() ?? "(default)"}, hotwords={settings.Hotwords.Count}");

        if (defaultAvailable)
        {
            CreateRecognizer(onDevice: false);
        }
        else if (onDeviceAvailable)
        {
            CreateRecognizer(onDevice: true);
        }
        else
        {
            throw new DictationException(DictationErrorKind.Unavailable, "SpeechRecognizer");
        }
        Listen();
    });

    public async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        if (_failures.LastFailure is { } prior && Current.Length == 0)
        {
            throw prior;
        }
        var hadSignal = false;
        await OnMainAsync(() =>
        {
            _stopping = true;
            hadSignal = _everReady || Current.Length > 0;
            if (_failures.LastFailure is { } failure && Current.Length == 0)
            {
                _final.TrySetException(failure);
                return;
            }
            _recognizer?.StopListening();
        }).ConfigureAwait(false);
        try
        {
            var timeout = hadSignal ? FinalTimeout : UnreadyStopTimeout;
            return (await _final.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (TimeoutException)
        {
            if (_failures.LastFailure is { } failure && Current.Length == 0) throw failure;
            if (!_everReady && Current.Length == 0)
            {
                Log.Warn(LogTag, "StopAsync timed out before recognizer ever became ready");
                throw new DictationException(DictationErrorKind.Unavailable, "System speech service did not respond");
            }
            // The recognizer never answered: what was heard so far is the text.
            return Current.Trim();
        }
    }

    public void Cancel()
    {
        _cancelled = true;
        Main.Post(() =>
        {
            _listenSequence++;
            try { _recognizer?.Cancel(); }
            catch (Exception) { }
        });
    }

    public ValueTask DisposeAsync()
    {
        _cancelled = true;
        Main.Post(DestroyRecognizer);
        return ValueTask.CompletedTask;
    }

    private string Current => _tail.Length == 0 ? _settled.ToString() : Join(_settled.ToString(), _tail);

    private void CreateRecognizer(bool onDevice)
    {
        DestroyRecognizer();
        _usingOnDevice = onDevice;
        if (onDevice) _triedOnDevice = true;
        SpeechRecognizer? recognizer;
        if (onDevice && OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            recognizer = SpeechRecognizer.CreateOnDeviceSpeechRecognizer(context);
        }
        else
        {
            recognizer = SpeechRecognizer.CreateSpeechRecognizer(context);
        }
        _recognizer = recognizer ?? throw new DictationException(DictationErrorKind.Unavailable, onDevice ? "OnDeviceSpeechRecognizer" : "SpeechRecognizer");
        _listener = new RecognitionListener(this);
        _recognizer.SetRecognitionListener(_listener);
    }

    private void DestroyRecognizer()
    {
        _listenSequence++;
        try { _recognizer?.Destroy(); }
        catch (Exception) { }
        _recognizer = null;
        _listener?.Dispose();
        _listener = null;
    }

    private void Listen()
    {
        if (_cancelled || _stopping || _recognizer is null) return;
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
        intent.PutExtra(RecognizerIntent.ExtraCallingPackage, context.PackageName);
        if (ResolveLanguageTag() is { Length: > 0 } language)
        {
            intent.PutExtra(RecognizerIntent.ExtraLanguage, language);
            intent.PutExtra(RecognizerIntent.ExtraLanguagePreference, language);
        }
        // A hint only (many recognizers ignore it): let a pause run long while the finger is down.
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, 8000L);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputPossiblyCompleteSilenceLengthMillis, 8000L);
        // Android 13+: words of the conversation to expect (file and class names).
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && settings.Hotwords.Count > 0)
        {
            intent.PutStringArrayListExtra(RecognizerIntent.ExtraBiasingStrings, [.. settings.Hotwords]);
        }
        _ready = false;
        var sequence = ++_listenSequence;
        Main.PostDelayed(() => OnReadyTimeout(sequence), ReadyTimeoutMs);
        try
        {
            _recognizer.StartListening(intent);
        }
        catch (Exception e)
        {
            Log.Warn(LogTag, $"StartListening threw {e.GetType().Name}: {e.Message}");
            FailSession(new DictationException(DictationErrorKind.Unavailable, e.Message, e));
        }
    }

    private void OnReadyTimeout(int sequence)
    {
        if (_cancelled || _stopping || _ready || sequence != _listenSequence) return;
        Log.Warn(LogTag, $"No OnReadyForSpeech/RMS callback within {ReadyTimeoutMs} ms (onDevice={_usingOnDevice})");
        if (TryFallbackToOnDevice("ready timeout")) return;
        try { _recognizer?.Cancel(); }
        catch (Exception) { }
        FailSession(new DictationException(DictationErrorKind.Unavailable, "System speech service did not respond"));
    }

    private void MarkReady(string source)
    {
        if (_ready) return;
        _ready = true;
        _everReady = true;
        _unreadyFailures = 0;
        Log.Debug(LogTag, $"Recognizer ready via {source} (onDevice={_usingOnDevice})");
    }

    private void OnReady() => MarkReady("OnReadyForSpeech");

    private void OnBeginning() => MarkReady("OnBeginningOfSpeech");

    private void OnLevel(float rmsDb)
    {
        if (_cancelled) return;
        MarkReady("OnRmsChanged");
        LevelChanged?.Invoke(PcmLevel.FromAndroidRmsDb(rmsDb));
    }

    private void OnPartial(string? text)
    {
        if (_cancelled || text is null) return;
        MarkReady("OnPartialResults");
        _tail = text;
        TextChanged?.Invoke(_settled.ToString(), Separator() + _tail);
    }

    private void OnResult(string? text)
    {
        if (_cancelled) return;
        MarkReady("OnResults");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var settled = Join(_settled.ToString(), text);
            _settled.Clear().Append(settled);
        }
        _tail = "";
        TextChanged?.Invoke(_settled.ToString(), "");
        if (_stopping)
        {
            _final.TrySetResult(_settled.ToString());
            return;
        }
        var sequence = _listenSequence;
        Main.PostDelayed(() =>
        {
            if (!_cancelled && !_stopping && sequence == _listenSequence) Listen();
        }, RestartDelayMs);
    }

    private void OnError(SpeechRecognizerError error)
    {
        if (_cancelled) return;
        Log.Warn(LogTag, $"OnError: {error} ({(int)error}), ready={_ready}, everReady={_everReady}, unreadyFailures={_unreadyFailures}, onDevice={_usingOnDevice}, stopping={_stopping}");
        switch (error)
        {
            // Nothing (more) was said in this round.
            case SpeechRecognizerError.NoMatch or SpeechRecognizerError.SpeechTimeout:
                if (_stopping)
                {
                    if (_everReady || Current.Length > 0) _final.TrySetResult(Current);
                    else FailSession(new DictationException(DictationErrorKind.Unavailable, error.ToString()));
                    return;
                }
                if (_ready)
                {
                    _unreadyFailures = 0;
                    ScheduleRestart(RestartDelayMs, cancelFirst: false);
                    return;
                }
                if (++_unreadyFailures <= 1)
                {
                    ScheduleRestart(RetryDelayMs, cancelFirst: true);
                    return;
                }
                if (TryFallbackToOnDevice(error.ToString())) return;
                FailSession(new DictationException(DictationErrorKind.Unavailable, error.ToString()));
                return;
            case SpeechRecognizerError.RecognizerBusy when !_stopping:
                if (++_unreadyFailures <= 2)
                {
                    ScheduleRestart(RetryDelayMs, cancelFirst: true);
                    return;
                }
                if (TryFallbackToOnDevice(error.ToString())) return;
                FailSession(new DictationException(DictationErrorKind.Unavailable, error.ToString()));
                return;
        }
        if (!_stopping && Current.Length == 0 && ShouldFallbackToOnDevice(error) && TryFallbackToOnDevice(error.ToString()))
        {
            return;
        }
        if (_stopping && Current.Length > 0)
        {
            _final.TrySetResult(Current);
            return;
        }
        FailSession(new DictationException(KindOf(error), error.ToString()));
    }

    private void ScheduleRestart(long delayMs, bool cancelFirst)
    {
        var sequence = _listenSequence;
        Main.PostDelayed(() =>
        {
            if (_cancelled || _stopping || sequence != _listenSequence) return;
            if (cancelFirst)
            {
                try { _recognizer?.Cancel(); }
                catch (Exception) { }
            }
            Listen();
        }, delayMs);
    }

    private bool TryFallbackToOnDevice(string reason)
    {
        if (_triedOnDevice || _usingOnDevice || _cancelled || _stopping) return false;
        if (!OperatingSystem.IsAndroidVersionAtLeast(31) || !SpeechRecognizer.IsOnDeviceRecognitionAvailable(context)) return false;
        Log.Info(LogTag, $"Falling back to on-device SpeechRecognizer after {reason}");
        try
        {
            _unreadyFailures = 0;
            CreateRecognizer(onDevice: true);
            Listen();
            return true;
        }
        catch (Exception e)
        {
            Log.Warn(LogTag, $"On-device SpeechRecognizer fallback failed: {e.Message}");
            return false;
        }
    }

    private void FailSession(DictationException failure)
    {
        _listenSequence++;
        if (_stopping) _final.TrySetException(failure);
        else _failures.Emit(failure);
    }

    private string? ResolveLanguageTag()
    {
        if (settings.LocaleTag is { } explicitTag) return explicitTag;
        try
        {
            var tag = Java.Util.Locale.Default?.ToLanguageTag();
            return string.IsNullOrWhiteSpace(tag) || tag.Equals("und", StringComparison.OrdinalIgnoreCase) ? null : tag;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? ReadDefaultRecognitionService()
    {
        try
        {
            return global::Android.Provider.Settings.Secure.GetString(context.ContentResolver, "voice_recognition_service");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ShouldFallbackToOnDevice(SpeechRecognizerError error) => (int)error switch
    {
        (int)SpeechRecognizerError.Network or (int)SpeechRecognizerError.NetworkTimeout or (int)SpeechRecognizerError.Server
            or (int)SpeechRecognizerError.Client or ErrorServerDisconnected or ErrorLanguageNotSupported or ErrorLanguageUnavailable => true,
        _ => false,
    };

    private static DictationErrorKind KindOf(SpeechRecognizerError error) => (int)error switch
    {
        (int)SpeechRecognizerError.InsufficientPermissions => DictationErrorKind.PermissionDenied,
        (int)SpeechRecognizerError.Network or (int)SpeechRecognizerError.NetworkTimeout or (int)SpeechRecognizerError.Server
            or ErrorServerDisconnected => DictationErrorKind.Network,
        (int)SpeechRecognizerError.Client or (int)SpeechRecognizerError.Audio
            or ErrorLanguageNotSupported or ErrorLanguageUnavailable or ErrorCannotCheckSupport => DictationErrorKind.Unavailable,
        _ => DictationErrorKind.Provider,
    };

    private string Separator() => _settled.Length > 0 && _tail.Length > 0 && NeedsSpace(_settled[^1], _tail[0]) ? " " : "";

    private static string Join(string settled, string next) =>
        settled.Length > 0 && next.Length > 0 && NeedsSpace(settled[^1], next[0]) ? settled + " " + next : settled + next;

    // Words in Latin letters need a space between them; Chinese runs straight on.
    private static bool NeedsSpace(char before, char after) => char.IsAsciiLetterOrDigit(before) && char.IsAsciiLetterOrDigit(after);

    private static Task OnMainAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Main.Post(() =>
        {
            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        });
        return done.Task;
    }

    private static string? First(Bundle? results) => results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition)?.FirstOrDefault();

    private sealed class RecognitionListener(AndroidSpeechSession session) : Java.Lang.Object, IRecognitionListener
    {
        public void OnBeginningOfSpeech() => session.OnBeginning();
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech() { }
        public void OnError(SpeechRecognizerError error) => session.OnError(error);
        public void OnEvent(int eventType, Bundle? @params) { }
        public void OnPartialResults(Bundle? partialResults) => session.OnPartial(First(partialResults));
        public void OnReadyForSpeech(Bundle? @params) => session.OnReady();
        public void OnResults(Bundle? results) => session.OnResult(First(results));
        public void OnRmsChanged(float rmsdB) => session.OnLevel(rmsdB);
    }
}

/// <summary>The microphone for the cloud engines: <see cref="AudioRecord"/> at 16 kHz mono PCM16, 100 ms per chunk.</summary>
internal sealed class AndroidAudioCapture : IAudioCapture
{
    private const int SampleRate = 16000;
    private const int ChunkBytes = SampleRate * 2 / 10;
    private AudioRecord? _record;
    private Thread? _pump;
    private volatile bool _running;

    public event Action<byte[]>? Captured;

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        _ = await AndroidPermissions.EnsureAsync(Manifest.Permission.RecordAudio).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await AndroidPermissions.EnsureAsync(Manifest.Permission.RecordAudio).ConfigureAwait(false))
        {
            throw new DictationException(DictationErrorKind.PermissionDenied, "RECORD_AUDIO");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var minimum = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, global::Android.Media.Encoding.Pcm16bit);
        var record = new AudioRecord(AudioSource.VoiceRecognition, SampleRate, ChannelIn.Mono, global::Android.Media.Encoding.Pcm16bit, Math.Max(minimum, ChunkBytes * 4));
        if (record.State != global::Android.Media.State.Initialized)
        {
            record.Release();
            record.Dispose();
            throw new DictationException(DictationErrorKind.Unavailable, "AudioRecord");
        }
        record.StartRecording();
        _record = record;
        _running = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "Octopus voice capture" };
        _pump.Start();
    }

    public Task StopAsync()
    {
        if (!_running) return Task.CompletedTask;
        _running = false;
        _pump?.Join(TimeSpan.FromMilliseconds(500));
        try
        {
            _record?.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _record?.Release();
        _record?.Dispose();
        _record = null;
    }

    private void Pump()
    {
        var buffer = new byte[ChunkBytes];
        while (_running && _record is { } record)
        {
            var read = record.Read(buffer, 0, buffer.Length);
            if (read < 0) break;
            if (read > 0) Captured?.Invoke(buffer.AsSpan(0, read).ToArray());
        }
    }
}
