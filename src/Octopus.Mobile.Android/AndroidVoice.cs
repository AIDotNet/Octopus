using System.Text;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Speech;
using Android.Views;
using Octopus.Mobile.Voice;

namespace Octopus.Mobile.Android;

/// <summary>Runtime permissions asked from the main activity (RECORD_AUDIO for voice input).</summary>
internal static class AndroidPermissions
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<int, TaskCompletionSource<bool>> Pending = [];
    private static int s_nextCode = 48100;

    public static Task<bool> EnsureAsync(string permission)
    {
        if (MainActivity.Current is not { } current || !current.TryGetTarget(out var activity)) return Task.FromResult(false);
        if (activity.CheckSelfPermission(permission) == Permission.Granted) return Task.FromResult(true);
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int code;
        lock (Gate)
        {
            code = s_nextCode++;
            Pending[code] = asked;
        }
        activity.RunOnUiThread(() => activity.RequestPermissions([permission], code));
        return asked.Task;
    }

    /// <summary>From <see cref="MainActivity.OnRequestPermissionsResult"/>.</summary>
    public static void Complete(int requestCode, Permission[] results)
    {
        TaskCompletionSource<bool>? asked;
        lock (Gate)
        {
            if (!Pending.Remove(requestCode, out asked)) return;
        }
        asked.TrySetResult(results.Length > 0 && results.All(result => result == Permission.Granted));
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
        SpeechRecognizer.IsRecognitionAvailable(context) ? DictationProblem.None : DictationProblem.NoSystemRecognizer;

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
}

/// <summary>
/// One dictation with <see cref="SpeechRecognizer"/>. The recognizer ends by itself after a pause; while the finger is
/// still down it is started again and the text runs on, so a pause to think does not end the dictation.
/// Everything with the recognizer happens on the main thread.
/// </summary>
internal sealed class AndroidSpeechSession(Context context, VoiceSettings settings) : IDictationSession
{
    private static readonly Handler Main = new(Looper.MainLooper!);
    private static readonly TimeSpan FinalTimeout = TimeSpan.FromSeconds(4);
    private readonly StringBuilder _settled = new();
    private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SpeechRecognizer? _recognizer;
    private RecognitionListener? _listener;
    private string _tail = "";
    private bool _stopping;
    private bool _cancelled;

    public event Action<float>? LevelChanged;
    public event Action<string, string>? TextChanged;
    public event Action<DictationException>? Failed;

    public Task StartAsync() => OnMainAsync(() =>
    {
        _recognizer = SpeechRecognizer.CreateSpeechRecognizer(context)
            ?? throw new DictationException(DictationErrorKind.Unavailable, "SpeechRecognizer");
        _listener = new RecognitionListener(this);
        _recognizer.SetRecognitionListener(_listener);
        Listen();
    });

    public async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        await OnMainAsync(() =>
        {
            _stopping = true;
            _recognizer?.StopListening();
        }).ConfigureAwait(false);
        try
        {
            return (await _final.Task.WaitAsync(FinalTimeout, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (TimeoutException)
        {
            // The recognizer never answered: what was heard so far is the text.
            return Current.Trim();
        }
    }

    public void Cancel()
    {
        _cancelled = true;
        Main.Post(() => _recognizer?.Cancel());
    }

    public ValueTask DisposeAsync()
    {
        _cancelled = true;
        Main.Post(() =>
        {
            _recognizer?.Destroy();
            _recognizer = null;
            _listener?.Dispose();
            _listener = null;
        });
        return ValueTask.CompletedTask;
    }

    private string Current => _tail.Length == 0 ? _settled.ToString() : Join(_settled.ToString(), _tail);

    private void Listen()
    {
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
        intent.PutExtra(RecognizerIntent.ExtraCallingPackage, context.PackageName);
        if (settings.LocaleTag is { } language)
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
        _recognizer?.StartListening(intent);
    }

    private void OnLevel(float rmsDb) => LevelChanged?.Invoke(PcmLevel.FromAndroidRmsDb(rmsDb));

    private void OnPartial(string? text)
    {
        if (_cancelled || text is null) return;
        _tail = text;
        TextChanged?.Invoke(_settled.ToString(), Separator() + _tail);
    }

    private void OnResult(string? text)
    {
        if (_cancelled) return;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var settled = Join(_settled.ToString(), text);
            _settled.Clear().Append(settled);
        }
        _tail = "";
        TextChanged?.Invoke(_settled.ToString(), "");
        if (_stopping) _final.TrySetResult(_settled.ToString());
        else Listen();
    }

    private void OnError(SpeechRecognizerError error)
    {
        if (_cancelled) return;
        switch (error)
        {
            // Nothing (more) was said in this round.
            case SpeechRecognizerError.NoMatch or SpeechRecognizerError.SpeechTimeout:
                if (_stopping) _final.TrySetResult(Current);
                else Listen();
                return;
            case SpeechRecognizerError.RecognizerBusy when !_stopping:
                Main.PostDelayed(Listen, 150);
                return;
        }
        if (_stopping && Current.Length > 0)
        {
            _final.TrySetResult(Current);
            return;
        }
        var failure = new DictationException(KindOf(error), error.ToString());
        if (_stopping) _final.TrySetException(failure);
        else Failed?.Invoke(failure);
    }

    private static DictationErrorKind KindOf(SpeechRecognizerError error) => error switch
    {
        SpeechRecognizerError.InsufficientPermissions => DictationErrorKind.PermissionDenied,
        SpeechRecognizerError.Network or SpeechRecognizerError.NetworkTimeout or SpeechRecognizerError.Server => DictationErrorKind.Network,
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
        public void OnBeginningOfSpeech() { }
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech() { }
        public void OnError(SpeechRecognizerError error) => session.OnError(error);
        public void OnEvent(int eventType, Bundle? @params) { }
        public void OnPartialResults(Bundle? partialResults) => session.OnPartial(First(partialResults));
        public void OnReadyForSpeech(Bundle? @params) { }
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
