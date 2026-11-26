using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;

namespace Octopus.Mobile.Voice;

/// <summary>
/// Me › Voice input: which engine turns speech into text (the phone's own by default), the keys of the cloud ones,
/// and how it behaves. Every change is kept a moment after the last keystroke, and when the page leaves.
/// </summary>
public sealed partial class VoiceSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);
    private static readonly HashSet<string> Kept =
    [
        nameof(Engine), nameof(LanguageIndex), nameof(Punctuation), nameof(Haptics),
        nameof(AliyunApiKey), nameof(AliyunRegionIndex), nameof(AliyunModel), nameof(AliyunWorkspaceId),
        nameof(VolcengineAppId), nameof(VolcengineAccessToken), nameof(VolcengineResourceId),
        nameof(OpenAiApiKey), nameof(OpenAiBaseUrl), nameof(OpenAiModel),
    ];

    private readonly VoiceService _voice;
    private readonly INavigator _navigator;
    private DispatcherTimer? _saveTimer;
    private bool _loading;
    private bool _dirty;

    public VoiceSettingsViewModel(VoiceService voice, INavigator navigator)
    {
        _voice = voice;
        _navigator = navigator;
        Show(voice.Settings);
        if (IsOctopus) _ = RefreshOctopusAsync();
        // The keys come from the keystore a moment after the app starts: show them once they are there.
        if (!voice.Loaded.IsCompleted) _ = ShowWhenLoadedAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystem), nameof(IsAliyun), nameof(IsVolcengine), nameof(IsOpenAi), nameof(IsOctopus), nameof(PrivacyText))]
    public partial VoiceEngineKind Engine { get; set; }

    /// <summary>What the account's server says about dictation through it: on or off, and today's minutes.</summary>
    [ObservableProperty] public partial string OctopusStatusText { get; private set; } = "";

    [ObservableProperty] public partial int LanguageIndex { get; set; }
    [ObservableProperty] public partial bool Punctuation { get; set; }
    [ObservableProperty] public partial bool Haptics { get; set; }
    [ObservableProperty] public partial string AliyunApiKey { get; set; } = "";
    [ObservableProperty] public partial int AliyunRegionIndex { get; set; }
    [ObservableProperty] public partial string AliyunModel { get; set; } = "";
    [ObservableProperty] public partial string AliyunWorkspaceId { get; set; } = "";
    [ObservableProperty] public partial string VolcengineAppId { get; set; } = "";
    [ObservableProperty] public partial string VolcengineAccessToken { get; set; } = "";
    [ObservableProperty] public partial string VolcengineResourceId { get; set; } = "";
    [ObservableProperty] public partial string OpenAiApiKey { get; set; } = "";
    [ObservableProperty] public partial string OpenAiBaseUrl { get; set; } = "";
    [ObservableProperty] public partial string OpenAiModel { get; set; } = "";

    public bool IsSystem => Engine == VoiceEngineKind.System;
    public bool IsAliyun => Engine == VoiceEngineKind.Aliyun;
    public bool IsVolcengine => Engine == VoiceEngineKind.Volcengine;
    public bool IsOpenAi => Engine == VoiceEngineKind.OpenAi;
    public bool IsOctopus => Engine == VoiceEngineKind.Octopus;

    /// <summary>The phone can dictate through the account at all (a head with a microphone and an account).</summary>
    public bool CanUseOctopus => _voice.HasOctopusEngine;

    /// <summary>The phone has a recognizer of its own that works.</summary>
    public bool IsSystemUsable => _voice.Check(VoiceEngineKind.System) != DictationProblem.NoSystemRecognizer;

    /// <summary>The cloud engines can record here (always on a phone).</summary>
    public bool CanRecord => _voice.CanRecord;

    public string SystemSubtitle => Text(IsSystemUsable ? MobileLangResourceKind.VoiceEngineSystemHint : MobileLangResourceKind.VoiceEngineSystemMissing);

    public IReadOnlyList<string> LanguageLabels { get; } =
        [Text(MobileLangResourceKind.VoiceLanguageAuto), Text(MobileLangResourceKind.VoiceLanguageChinese), Text(MobileLangResourceKind.VoiceLanguageEnglish)];

    public IReadOnlyList<string> RegionLabels { get; } =
        [Text(MobileLangResourceKind.VoiceAliyunRegionChina), Text(MobileLangResourceKind.VoiceAliyunRegionIntl)];

    public string PrivacyText => Engine switch
    {
        VoiceEngineKind.System => Text(MobileLangResourceKind.VoicePrivacySystem),
        VoiceEngineKind.Octopus => Text(MobileLangResourceKind.VoicePrivacyOctopus),
        _ => string.Format(Text(MobileLangResourceKind.VoicePrivacyCloud), VoiceService.NameOf(Engine)),
    };

    [RelayCommand]
    private void SelectEngine(string? kind)
    {
        if (!Enum.TryParse<VoiceEngineKind>(kind, out var engine)) return;
        if (engine == VoiceEngineKind.System && !IsSystemUsable) return;
        if (engine != VoiceEngineKind.System && !CanRecord) return;
        if (engine == VoiceEngineKind.Octopus && !CanUseOctopus) return;
        Engine = engine;
        if (engine == VoiceEngineKind.Octopus) _ = RefreshOctopusAsync();
    }

    [RelayCommand]
    private void Back()
    {
        Flush();
        _navigator.Back();
    }

    /// <summary>Keeps what is not kept yet; the page calls it when it leaves.</summary>
    public void Flush()
    {
        _saveTimer?.Stop();
        if (!_dirty) return;
        _dirty = false;
        _ = _voice.SaveAsync(Collect());
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || !Kept.Contains(e.PropertyName)) return;
        _dirty = true;
        if (Application.Current is null)
        {
            Flush();
            return;
        }
        _saveTimer ??= new DispatcherTimer(SaveDelay, DispatcherPriority.Background, (_, _) => Flush());
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task RefreshOctopusAsync()
    {
        if (_voice.Check(VoiceEngineKind.Octopus) == DictationProblem.NotSignedIn)
        {
            OctopusStatusText = Text(MobileLangResourceKind.VoiceProblemNotSignedIn);
            return;
        }
        OctopusStatusText = Text(MobileLangResourceKind.VoiceOctopusStatusLoading);
        var status = await _voice.GetOctopusStatusAsync(CancellationToken.None);
        OctopusStatusText = status switch
        {
            null => Text(MobileLangResourceKind.VoiceOctopusStatusUnknown),
            { Available: false } => Text(MobileLangResourceKind.VoiceErrorServerOff),
            { DailySeconds: <= 0 } => Text(MobileLangResourceKind.VoiceOctopusStatusUnlimited),
            _ => string.Format(Text(MobileLangResourceKind.VoiceOctopusStatusQuota), (status.UsedSeconds + 59) / 60, status.DailySeconds / 60),
        };
    }

    private async Task ShowWhenLoadedAsync()
    {
        await _voice.Loaded;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_dirty) Show(_voice.Settings);
        });
    }

    private void Show(VoiceSettings settings)
    {
        _loading = true;
        try
        {
            Engine = settings.Engine;
            LanguageIndex = (int)settings.Language;
            Punctuation = settings.Punctuation;
            Haptics = settings.Haptics;
            AliyunApiKey = settings.AliyunApiKey;
            AliyunRegionIndex = (int)settings.AliyunRegion;
            AliyunModel = settings.AliyunModel;
            AliyunWorkspaceId = settings.AliyunWorkspaceId;
            VolcengineAppId = settings.VolcengineAppId;
            VolcengineAccessToken = settings.VolcengineAccessToken;
            VolcengineResourceId = settings.VolcengineResourceId;
            OpenAiApiKey = settings.OpenAiApiKey;
            OpenAiBaseUrl = settings.OpenAiBaseUrl;
            OpenAiModel = settings.OpenAiModel;
        }
        finally
        {
            _loading = false;
        }
    }

    private VoiceSettings Collect() => new()
    {
        Engine = Engine,
        Language = Enum.IsDefined((VoiceLanguage)LanguageIndex) ? (VoiceLanguage)LanguageIndex : VoiceLanguage.Auto,
        Punctuation = Punctuation,
        Haptics = Haptics,
        AliyunApiKey = AliyunApiKey.Trim(),
        AliyunRegion = AliyunRegionIndex == 1 ? AliyunRegion.International : AliyunRegion.China,
        AliyunModel = OrDefault(AliyunModel, VoiceSettings.DefaultAliyunModel),
        AliyunWorkspaceId = AliyunWorkspaceId.Trim(),
        VolcengineAppId = VolcengineAppId.Trim(),
        VolcengineAccessToken = VolcengineAccessToken.Trim(),
        VolcengineResourceId = OrDefault(VolcengineResourceId, VoiceSettings.DefaultVolcengineResourceId),
        OpenAiApiKey = OpenAiApiKey.Trim(),
        OpenAiBaseUrl = OrDefault(OpenAiBaseUrl, VoiceSettings.DefaultOpenAiBaseUrl),
        OpenAiModel = OrDefault(OpenAiModel, VoiceSettings.DefaultOpenAiModel),
    };

    private static string OrDefault(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Text(MobileLangResourceKind kind) => MobileLangResources.Get(kind);
}
