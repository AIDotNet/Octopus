using Octopus.Mobile.Localization;
using Octopus.Mobile.Remote;

namespace Octopus.Mobile.Voice;

/// <summary>
/// Voice input of the app: the settings, the engine they pick, and starting a dictation with it. A platform head makes
/// one with what the phone has (<see cref="MobileHost.Voice"/>); without a head (tests, the desktop preview) nothing can
/// record and the microphone button stays hidden.
/// </summary>
public sealed class VoiceService
{
    private readonly VoiceSettingsStore _store;
    private readonly Dictionary<VoiceEngineKind, IDictationEngine> _engines = [];
    private readonly Task _loaded;
    private readonly Func<IAudioCapture>? _audio;
    private readonly AccountEndpoint? _account;
    private readonly Func<HttpMessageHandler> _http = () => Octopus.Remote.Relay.RelayHttp.CreateHandler();

    /// <param name="store">Where the settings and keys are kept; null keeps them in memory only.</param>
    /// <param name="system">The phone's own recognizer; null when the head has none.</param>
    /// <param name="audio">The microphone for the cloud engines; null when the head cannot record.</param>
    /// <param name="http">The HTTP stack (Android routes through its own, for OEM certificate stores and proxies).</param>
    /// <param name="account">The account's server and token, for Octopus cloud recognition; null without an account.</param>
    /// <param name="signedIn">Whether the phone is signed in to an account right now.</param>
    public VoiceService(IMobileStore? store = null, IDictationEngine? system = null, Func<IAudioCapture>? audio = null, Func<HttpMessageHandler>? http = null,
        AccountEndpoint? account = null, Func<bool>? signedIn = null)
    {
        _audio = audio;
        _account = account;
        _http = http ?? (() => Octopus.Remote.Relay.RelayHttp.CreateHandler());
        _store = new VoiceSettingsStore(store);
        if (system is not null) _engines[VoiceEngineKind.System] = system;
        if (audio is not null)
        {
            _engines[VoiceEngineKind.Aliyun] = new AliyunDictationEngine(audio);
            _engines[VoiceEngineKind.Volcengine] = new VolcengineDictationEngine(audio);
            _engines[VoiceEngineKind.OpenAi] = new OpenAiDictationEngine(audio, http);
            if (account is not null) _engines[VoiceEngineKind.Octopus] = new OctopusDictationEngine(audio, account, signedIn ?? (() => true));
        }
        _loaded = LoadAsync();
    }

    /// <summary>For tests: engines of their own choosing, in memory.</summary>
    internal VoiceService(IReadOnlyDictionary<VoiceEngineKind, IDictationEngine> engines, VoiceSettings? settings = null)
    {
        _store = new VoiceSettingsStore(null);
        foreach (var (kind, engine) in engines) _engines[kind] = engine;
        Settings = settings ?? new VoiceSettings();
        _loaded = Task.CompletedTask;
    }

    public VoiceSettings Settings { get; private set; } = new();

    /// <summary>Some engine could work on this head: the microphone button and the settings row show.</summary>
    public bool IsSupported => _engines.Count > 0;

    /// <summary>The phone has a recognizer of its own (it may still be unusable, see <see cref="Check(VoiceEngineKind)"/>).</summary>
    public bool HasSystemEngine => _engines.ContainsKey(VoiceEngineKind.System);

    /// <summary>The cloud engines can record on this head.</summary>
    public bool CanRecord => _engines.Keys.Any(kind => kind != VoiceEngineKind.System);

    /// <summary>Raised on the thread that saved the settings.</summary>
    public event Action? Changed;

    public Task Loaded => _loaded;

    public DictationProblem Check() => Check(Settings.Engine);

    public DictationProblem Check(VoiceEngineKind kind)
    {
        if (!_engines.TryGetValue(kind, out var engine))
        {
            return kind == VoiceEngineKind.System ? DictationProblem.NoSystemRecognizer : DictationProblem.NoMicrophone;
        }
        return engine.Check(Settings);
    }

    /// <summary>Whether this phone can dictate through the Octopus account at all (it has a microphone and an account seam).</summary>
    public bool HasOctopusEngine => _engines.ContainsKey(VoiceEngineKind.Octopus);

    /// <summary>The account server's voice status (<c>GET /v1/voice</c>); null when it cannot be asked.</summary>
    public Task<OctopusVoiceStatus?> GetOctopusStatusAsync(CancellationToken cancellationToken) =>
        _account is null ? Task.FromResult<OctopusVoiceStatus?>(null) : OctopusDictationEngine.StatusAsync(_account, _http, cancellationToken);

    /// <summary>Warms up permissions for the active engine when entering voice mode, before holding to talk.</summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        await _loaded.ConfigureAwait(false);
        var kind = Settings.Engine;
        if (!_engines.TryGetValue(kind, out var engine)) return;
        await engine.PrepareAsync(cancellationToken).ConfigureAwait(false);
        if (kind != VoiceEngineKind.System && _audio is not null)
        {
            await using var capture = _audio();
            await capture.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <param name="hotwords">Words of the conversation the engine should expect.</param>
    public async Task<IDictationSession> StartAsync(IReadOnlyList<string> hotwords, CancellationToken cancellationToken)
    {
        await _loaded.ConfigureAwait(false);
        var settings = Settings.Clone();
        settings.Hotwords = hotwords;
        if (!_engines.TryGetValue(settings.Engine, out var engine))
        {
            throw new DictationException(DictationErrorKind.Unavailable, NameOf(settings.Engine));
        }
        return await engine.StartAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps <paramref name="settings"/> and tells whoever shows them.</summary>
    public async Task SaveAsync(VoiceSettings settings)
    {
        Settings = settings.Clone();
        Changed?.Invoke();
        try
        {
            await _store.SaveAsync(Settings).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Not kept for the next start; this start still uses them.
        }
    }

    /// <summary>The engine's name as the person sees it.</summary>
    public static string NameOf(VoiceEngineKind kind) => MobileLangResources.Get(kind switch
    {
        VoiceEngineKind.Aliyun => MobileLangResourceKind.VoiceEngineAliyun,
        VoiceEngineKind.Volcengine => MobileLangResourceKind.VoiceEngineVolcengine,
        VoiceEngineKind.OpenAi => MobileLangResourceKind.VoiceEngineOpenAi,
        VoiceEngineKind.Octopus => MobileLangResourceKind.VoiceEngineOctopus,
        _ => MobileLangResourceKind.VoiceEngineSystem,
    });

    private async Task LoadAsync()
    {
        try
        {
            Settings = await _store.LoadAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            Settings = new VoiceSettings();
        }
        Changed?.Invoke();
    }
}
