using System.Runtime.InteropServices;
using AVFoundation;
using Octopus.Mobile.Voice;
using Speech;

namespace Octopus.Mobile.iOS;

/// <summary>The app's audio session while voice input records, and the microphone permission.</summary>
internal static class IosAudioSession
{
    public static async Task EnsureMicrophoneAsync()
    {
        bool granted;
        if (OperatingSystem.IsIOSVersionAtLeast(17))
        {
            granted = AVAudioApplication.SharedInstance.RecordPermission switch
            {
                AVAudioApplicationRecordPermission.Granted => true,
                AVAudioApplicationRecordPermission.Denied => false,
                _ => await AVAudioApplication.RequestRecordPermissionAsync().ConfigureAwait(false),
            };
        }
        else
        {
            var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA1422 // Replaced by AVAudioApplication on iOS 17; this branch is for iOS 15–16.
            AVAudioSession.SharedInstance().RequestRecordPermission(asked.SetResult);
#pragma warning restore CA1422
            granted = await asked.Task.ConfigureAwait(false);
        }
        if (!granted) throw new DictationException(DictationErrorKind.PermissionDenied, "microphone");
    }

    public static void Activate()
    {
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.DuckOthers | AVAudioSessionCategoryOptions.DefaultToSpeaker, out _);
        session.SetMode(AVAudioSessionMode.Measurement, out _);
        if (!session.SetActive(true, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var error))
        {
            throw new DictationException(DictationErrorKind.Unavailable, error?.LocalizedDescription ?? "AVAudioSession");
        }
    }

    public static void Deactivate() =>
        AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);

    /// <summary>Taps the input; iOS 27 adds the overload that reports a failure instead of raising.</summary>
    public static void InstallTap(AVAudioNode node, uint bufferSize, AVAudioFormat format, AVAudioNodeTapBlock block)
    {
        if (OperatingSystem.IsIOSVersionAtLeast(27))
        {
            if (!node.InstallTapOnBus(0, bufferSize, format, out var error, block))
            {
                throw new DictationException(DictationErrorKind.Unavailable, error?.LocalizedDescription ?? "installTap");
            }
            return;
        }
        node.InstallTapOnBus(0, bufferSize, format, block);
    }

    /// <summary>Loudness of the first channel of a float buffer, as the waveform draws it.</summary>
    public static float LevelOf(AVAudioPcmBuffer buffer)
    {
        if (buffer.FloatChannelData == IntPtr.Zero || buffer.FrameLength == 0) return 0;
        var samples = new float[buffer.FrameLength];
        Marshal.Copy(Marshal.ReadIntPtr(buffer.FloatChannelData), samples, 0, samples.Length);
        return PcmLevel.Of(samples);
    }
}

/// <summary>Taps the phone can feel.</summary>
internal static class IosHaptics
{
    public static void Perform(HapticKind kind) => UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
    {
        if (kind == HapticKind.Error)
        {
            using var notification = new UINotificationFeedbackGenerator();
            notification.NotificationOccurred(UINotificationFeedbackType.Error);
            return;
        }
        var style = kind == HapticKind.Light ? UIImpactFeedbackStyle.Light : UIImpactFeedbackStyle.Medium;
        using var impact = OperatingSystem.IsIOSVersionAtLeast(17, 5) && KeyWindow() is { } window
            ? UIImpactFeedbackGenerator.GetFeedbackGenerator(style, window)
            : Legacy(style);
        impact.ImpactOccurred();
    });

    private static UIWindow? KeyWindow() =>
        UIApplication.SharedApplication.ConnectedScenes.ToArray().OfType<UIWindowScene>().SelectMany(scene => scene.Windows).FirstOrDefault(window => window.IsKeyWindow);

#pragma warning disable CA1422 // The view-bound generator replaces it on iOS 17.5; before that this is the only one.
    private static UIImpactFeedbackGenerator Legacy(UIImpactFeedbackStyle style) => new(style);
#pragma warning restore CA1422
}

/// <summary>Apple's speech recognition (<see cref="SFSpeechRecognizer"/>): on the phone where the language allows it.</summary>
internal sealed class IosSpeechDictation : IDictationEngine
{
    public DictationProblem Check(VoiceSettings settings) =>
        RecognizerFor(settings) is { Available: true } ? DictationProblem.None : DictationProblem.NoSystemRecognizer;

    public async Task<IDictationSession> StartAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync().ConfigureAwait(false))
        {
            throw new DictationException(DictationErrorKind.PermissionDenied, "speech recognition");
        }
        await IosAudioSession.EnsureMicrophoneAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (RecognizerFor(settings) is not { Available: true } recognizer)
        {
            throw new DictationException(DictationErrorKind.Unavailable, "SFSpeechRecognizer");
        }
        var session = new IosSpeechSession(recognizer, settings);
        try
        {
            session.Start();
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }

    private static SFSpeechRecognizer? RecognizerFor(VoiceSettings settings)
    {
        try
        {
            return settings.LocaleTag is { } tag ? new SFSpeechRecognizer(new NSLocale(tag)) : new SFSpeechRecognizer();
        }
        catch (Exception e) when (e is ArgumentException or ObjCRuntime.ObjCException or InvalidOperationException)
        {
            // No recognizer for this language on this phone.
            return null;
        }
    }

    private static Task<bool> AuthorizeAsync()
    {
        var status = SFSpeechRecognizer.AuthorizationStatus;
        if (status != SFSpeechRecognizerAuthorizationStatus.NotDetermined) return Task.FromResult(status == SFSpeechRecognizerAuthorizationStatus.Authorized);
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SFSpeechRecognizer.RequestAuthorization(answer => asked.TrySetResult(answer == SFSpeechRecognizerAuthorizationStatus.Authorized));
        return asked.Task;
    }
}

/// <summary>
/// One dictation: the microphone through <see cref="AVAudioEngine"/> into a buffer recognition request. The recognizer
/// gives up after a long silence; while the finger is still down a new request carries on after the text so far.
/// </summary>
internal sealed class IosSpeechSession(SFSpeechRecognizer recognizer, VoiceSettings settings) : IDictationSession
{
    // kAFAssistantErrorDomain: 1110 "no speech detected", 203 "retry" (nothing usable heard).
    private const long NoSpeechCode = 1110;
    private const long RetryCode = 203;
    private static readonly TimeSpan FinalTimeout = TimeSpan.FromSeconds(4);
    private readonly AVAudioEngine _engine = new();
    private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private SFSpeechAudioBufferRecognitionRequest _request = new();
    private SFSpeechRecognitionTask? _task;
    private string _settled = "";
    private string _tail = "";
    private bool _audioStopped;
    private bool _stopping;
    private bool _cancelled;

    public event Action<float>? LevelChanged;
    public event Action<string, string>? TextChanged;
    public event Action<DictationException>? Failed;

    public void Start()
    {
        IosAudioSession.Activate();
        var input = _engine.InputNode;
        IosAudioSession.InstallTap(input, 1024, input.GetBusOutputFormat(0), (buffer, _) =>
        {
            SFSpeechAudioBufferRecognitionRequest request;
            lock (_gate) request = _request;
            request.Append(buffer);
            LevelChanged?.Invoke(IosAudioSession.LevelOf(buffer));
        });
        _engine.Prepare();
        if (!_engine.StartAndReturnError(out var error))
        {
            throw new DictationException(DictationErrorKind.Unavailable, error?.LocalizedDescription ?? "AVAudioEngine");
        }
        Recognize();
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        StopAudio();
        lock (_gate) _request.EndAudio();
        try
        {
            return (await _final.Task.WaitAsync(FinalTimeout, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (TimeoutException)
        {
            _task?.Finish();
            return Join(_settled, _tail).Trim();
        }
    }

    public void Cancel()
    {
        _cancelled = true;
        _task?.Cancel();
        StopAudio();
    }

    public ValueTask DisposeAsync()
    {
        Cancel();
        _task?.Dispose();
        _engine.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Recognize()
    {
        var request = new SFSpeechAudioBufferRecognitionRequest { ShouldReportPartialResults = true };
        if (OperatingSystem.IsIOSVersionAtLeast(16)) request.AddsPunctuation = settings.Punctuation;
        // Words of the conversation to expect (file and class names); Apple suggests at most 100.
        if (settings.Hotwords.Count > 0) request.ContextualStrings = [.. settings.Hotwords.Take(100)];
        lock (_gate) _request = request;
        _task = recognizer.GetRecognitionTask(request, OnResult);
    }

    private void OnResult(SFSpeechRecognitionResult? result, NSError? error)
    {
        if (_cancelled) return;
        if (result is not null)
        {
            _tail = result.BestTranscription.FormattedString;
            TextChanged?.Invoke(_settled, Separator(_settled, _tail) + _tail);
            if (result.Final)
            {
                if (_stopping)
                {
                    _final.TrySetResult(Join(_settled, _tail));
                    return;
                }
                // Ended by itself (a long pause) while the finger is still down: carry on in a new request.
                _settled = Join(_settled, _tail);
                _tail = "";
                Recognize();
                return;
            }
        }
        if (error is null) return;
        var heard = Join(_settled, _tail);
        if (_stopping)
        {
            _final.TrySetResult(heard);
            return;
        }
        if ((long)error.Code is NoSpeechCode or RetryCode)
        {
            _settled = heard;
            _tail = "";
            Recognize();
            return;
        }
        Failed?.Invoke(new DictationException(DictationErrorKind.Provider, error.LocalizedDescription));
    }

    private void StopAudio()
    {
        if (_audioStopped) return;
        _audioStopped = true;
        _engine.InputNode.RemoveTapOnBus(0);
        _engine.Stop();
        IosAudioSession.Deactivate();
    }

    private static string Separator(string settled, string next) =>
        settled.Length > 0 && next.Length > 0 && char.IsAsciiLetterOrDigit(settled[^1]) && char.IsAsciiLetterOrDigit(next[0]) ? " " : "";

    private static string Join(string settled, string next) => settled + Separator(settled, next) + next;
}

/// <summary>The microphone for the cloud engines: the input converted to 16 kHz mono PCM16.</summary>
internal sealed class IosAudioCapture : IAudioCapture
{
    private const double SampleRate = 16000;
    private AVAudioEngine? _engine;
    private AVAudioConverter? _converter;
    private AVAudioFormat? _target;

    public event Action<byte[]>? Captured;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await IosAudioSession.EnsureMicrophoneAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        IosAudioSession.Activate();
        var engine = new AVAudioEngine();
        var input = engine.InputNode;
        var format = input.GetBusOutputFormat(0);
        _target = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, SampleRate, 1, false);
        _converter = new AVAudioConverter(format, _target);
        IosAudioSession.InstallTap(input, 4096, format, (buffer, _) => Convert(buffer));
        engine.Prepare();
        if (!engine.StartAndReturnError(out var error))
        {
            input.RemoveTapOnBus(0);
            engine.Dispose();
            throw new DictationException(DictationErrorKind.Unavailable, error?.LocalizedDescription ?? "AVAudioEngine");
        }
        _engine = engine;
    }

    public Task StopAsync()
    {
        if (_engine is { } engine)
        {
            _engine = null;
            engine.InputNode.RemoveTapOnBus(0);
            engine.Stop();
            engine.Dispose();
            IosAudioSession.Deactivate();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _converter?.Dispose();
        _converter = null;
        _target?.Dispose();
        _target = null;
    }

    private void Convert(AVAudioPcmBuffer buffer)
    {
        if (_converter is not { } converter || _target is not { } target || buffer.FrameLength == 0) return;
        var capacity = (uint)(buffer.FrameLength * SampleRate / buffer.Format.SampleRate) + 64;
        using var output = new AVAudioPcmBuffer(target, capacity);
        var given = false;
        converter.ConvertToBuffer(output, out var error, (uint _, out AVAudioConverterInputStatus status) =>
        {
            if (given)
            {
                status = AVAudioConverterInputStatus.NoDataNow;
                // nil with NoDataNow: this buffer is used up (the binding's type does not say it may be nil).
                return null!;
            }
            given = true;
            status = AVAudioConverterInputStatus.HaveData;
            return buffer;
        });
        if (error is not null || output.FrameLength == 0 || output.Int16ChannelData == IntPtr.Zero) return;
        var bytes = new byte[output.FrameLength * 2];
        Marshal.Copy(Marshal.ReadIntPtr(output.Int16ChannelData), bytes, 0, bytes.Length);
        Captured?.Invoke(bytes);
    }
}
