namespace Octopus.Mobile.Voice;

/// <summary>
/// Where speech becomes text (design/ui/移动端输入框重写与语音输入方案.html): the phone's own recognizer, or one of the
/// cloud services the person brings a key for. The person picks one in Me › Voice input; the system one is the default.
/// </summary>
public enum VoiceEngineKind
{
    System,
    Aliyun,
    Volcengine,
    OpenAi,
    /// <summary>Through the Octopus account: the server forwards the audio with its own key (design/server/05 §2「语音输入」).</summary>
    Octopus,
}

/// <summary>Why the chosen engine cannot start right now; <see cref="None"/> when it can.</summary>
public enum DictationProblem
{
    None,
    /// <summary>The phone has no recognizer of its own (an Android without Google's speech service, HarmonyOS).</summary>
    NoSystemRecognizer,
    /// <summary>A cloud engine without its key (or app id) filled in.</summary>
    NotConfigured,
    /// <summary>This head cannot record (the desktop preview): only the system engine could work, and there is none.</summary>
    NoMicrophone,
    /// <summary>Octopus cloud recognition without an Octopus account on this phone.</summary>
    NotSignedIn,
}

/// <summary>What went wrong once a dictation was under way.</summary>
public enum DictationErrorKind
{
    PermissionDenied,
    Unavailable,
    Network,
    Auth,
    Provider,
    NoSpeech,
    /// <summary>The Octopus server does not know this sign-in (any more).</summary>
    NotSignedIn,
    /// <summary>The Octopus server has no recognition set up.</summary>
    ServerOff,
    /// <summary>Today's allowance of the account is used up, or it dictates too much at once.</summary>
    Quota,
}

/// <summary>A dictation that could not start or did not finish; <see cref="Exception.Message"/> is the provider's own words.</summary>
public sealed class DictationException : Exception
{
    public DictationException(DictationErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public DictationErrorKind Kind { get; }
}

/// <summary>One engine: the system recognizer (set by a platform head) or a cloud client (Voice/Cloud*).</summary>
public interface IDictationEngine
{
    DictationProblem Check(VoiceSettings settings);

    /// <summary>
    /// Optional warm-up when the person switches into voice mode (e.g. requesting microphone permission before they
    /// hold "hold to talk", so the system permission dialog does not steal pointer capture).
    /// </summary>
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Starts listening. Asks for the microphone (and speech) permission the first time; throws
    /// <see cref="DictationException"/> when that is refused or the engine cannot start.
    /// </summary>
    Task<IDictationSession> StartAsync(VoiceSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// One press of "hold to talk". The events may come on any thread. Text comes as what is settled plus a tail that may
/// still change, so the bubble can draw the tail fainter.
/// </summary>
public interface IDictationSession : IAsyncDisposable
{
    /// <summary>How loud it is, 0 to 1, about twenty times a second.</summary>
    event Action<float>? LevelChanged;

    /// <summary>The text so far: settled, then the tail that may still change.</summary>
    event Action<string, string>? TextChanged;

    /// <summary>The engine gave up while listening (the connection dropped, the key was refused).</summary>
    event Action<DictationException>? Failed;

    /// <summary>The finger is lifted: stops listening and returns the final text (empty when nothing was heard).</summary>
    Task<string> StopAsync(CancellationToken cancellationToken);

    /// <summary>Thrown away: no text, the audio is dropped.</summary>
    void Cancel();
}

/// <summary>
/// The phone's microphone as the cloud engines need it: 16 kHz, mono, 16-bit little-endian PCM in chunks of about
/// 100 ms. A platform head provides one (<see cref="MobileHost.Voice"/>); the system recognizers record by themselves.
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    /// <summary>Raised on a background thread with each chunk.</summary>
    event Action<byte[]>? Captured;

    /// <summary>Optional permission warm-up when switching to voice mode before holding to talk.</summary>
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Asks for the microphone the first time; <see cref="DictationException"/> with PermissionDenied when refused.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();
}

/// <summary>
/// Buffers a dictation failure that fires before the caller attaches <see cref="IDictationSession.Failed"/>, and
/// replays it as soon as a subscriber arrives so early engine errors are never silently dropped.
/// </summary>
public sealed class DictationFailureRelay
{
    private readonly Lock _gate = new();
    private Action<DictationException>? _handlers;
    private DictationException? _pending;

    /// <summary>The first failure emitted so far, if any.</summary>
    public DictationException? LastFailure { get; private set; }

    public void Add(Action<DictationException>? handler)
    {
        if (handler is null) return;
        DictationException? pending;
        lock (_gate)
        {
            _handlers += handler;
            pending = _pending;
            _pending = null;
        }
        if (pending is not null) handler(pending);
    }

    public void Remove(Action<DictationException>? handler)
    {
        if (handler is null) return;
        lock (_gate) _handlers -= handler;
    }

    public void Emit(DictationException failure)
    {
        Action<DictationException>? handlers;
        lock (_gate)
        {
            LastFailure ??= failure;
            handlers = _handlers;
            if (handlers is null)
            {
                _pending ??= failure;
                return;
            }
        }
        handlers(failure);
    }
}

/// <summary>A tap the phone can feel; a head without haptics ignores it.</summary>
public enum HapticKind
{
    Light,
    Medium,
    Error,
}

/// <summary>Loudness of PCM as the waveform draws it.</summary>
public static class PcmLevel
{
    /// <summary>16-bit little-endian samples to 0–1: −50 dBFS and below is silence, 0 dBFS is full.</summary>
    public static float Of(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        if (samples == 0) return 0;
        double sum = 0;
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(pcm16[2 * i] | (pcm16[(2 * i) + 1] << 8)) / 32768.0;
            sum += sample * sample;
        }
        return FromRms(Math.Sqrt(sum / samples));
    }

    /// <summary>Float samples (−1 to 1), as iOS hands them over.</summary>
    public static float Of(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return FromRms(Math.Sqrt(sum / samples.Length));
    }

    /// <summary>Android's recognizer reports roughly −2 to 10 dB.</summary>
    public static float FromAndroidRmsDb(float rmsDb) => Math.Clamp((rmsDb + 2f) / 12f, 0f, 1f);

    private static float FromRms(double rms)
    {
        if (rms <= 0) return 0;
        var db = 20 * Math.Log10(rms);
        return (float)Math.Clamp((db + 50) / 50, 0, 1);
    }
}
