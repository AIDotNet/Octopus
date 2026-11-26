using System.Text.Json;
using System.Text.Json.Serialization;
using Octopus.Mobile.Remote;

namespace Octopus.Mobile.Voice;

/// <summary>The language the person speaks; <see cref="Auto"/> lets the engine decide (mixed Chinese and English).</summary>
public enum VoiceLanguage
{
    Auto,
    Chinese,
    English,
}

/// <summary>Which Model Studio endpoint an Alibaba Cloud key belongs to: keys of the two regions are not interchangeable.</summary>
public enum AliyunRegion
{
    China,
    International,
}

/// <summary>
/// Voice input as the person set it up on this phone. Kept on the phone only, never synced with the account: what the
/// phone's own recognizer can do differs from phone to phone. The keys are secrets (<see cref="IMobileStore"/>); the
/// rest is a plain file next to them.
/// </summary>
public sealed class VoiceSettings
{
    public const string DefaultAliyunModel = "fun-asr-realtime";
    public const string DefaultVolcengineResourceId = "volc.bigasr.sauc.duration";
    public const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";
    // gpt-4o-transcribe and gpt-4o-mini-transcribe are deprecated (shut down 2027-02-26).
    public const string DefaultOpenAiModel = "gpt-transcribe";

    public VoiceEngineKind Engine { get; set; } = VoiceEngineKind.System;
    public VoiceLanguage Language { get; set; } = VoiceLanguage.Auto;
    public bool Punctuation { get; set; } = true;
    public bool Haptics { get; set; } = true;

    public AliyunRegion AliyunRegion { get; set; } = AliyunRegion.China;
    public string AliyunModel { get; set; } = DefaultAliyunModel;

    /// <summary>
    /// The Model Studio workspace: with it the dictation goes to the workspace's own endpoint, which Alibaba now
    /// recommends; without it, to the older shared one (dashscope.aliyuncs.com).
    /// </summary>
    public string AliyunWorkspaceId { get; set; } = "";

    /// <summary>
    /// The older Volcengine console's "APP ID" (sent as X-Api-App-Key, with the token as X-Api-Access-Key). Empty for
    /// the newer console, whose single API key goes in <see cref="VolcengineAccessToken"/> (sent as X-Api-Key).
    /// </summary>
    public string VolcengineAppId { get; set; } = "";
    public string VolcengineResourceId { get; set; } = DefaultVolcengineResourceId;

    /// <summary>Any OpenAI-compatible /audio/transcriptions service; the official one by default.</summary>
    public string OpenAiBaseUrl { get; set; } = DefaultOpenAiBaseUrl;
    public string OpenAiModel { get; set; } = DefaultOpenAiModel;

    [JsonIgnore] public string AliyunApiKey { get; set; } = "";
    /// <summary>The Volcengine API key (newer console) or Access Token (older console, with <see cref="VolcengineAppId"/>).</summary>
    [JsonIgnore] public string VolcengineAccessToken { get; set; } = "";
    [JsonIgnore] public string OpenAiApiKey { get; set; } = "";

    /// <summary>
    /// Words of the conversation the engine should expect (file names, class names): given for one dictation, never kept.
    /// </summary>
    [JsonIgnore] public IReadOnlyList<string> Hotwords { get; set; } = [];

    /// <summary>A copy for one dictation, so a change in the settings page does not reach a session under way.</summary>
    public VoiceSettings Clone() => (VoiceSettings)MemberwiseClone();

    /// <summary>"zh-CN" / "en-US" for the chosen language; null for automatic.</summary>
    public string? LocaleTag => Language switch
    {
        VoiceLanguage.Chinese => "zh-CN",
        VoiceLanguage.English => "en-US",
        _ => null,
    };

    /// <summary>"zh" / "en" for the chosen language; null for automatic.</summary>
    public string? LanguageCode => Language switch
    {
        VoiceLanguage.Chinese => "zh",
        VoiceLanguage.English => "en",
        _ => null,
    };
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(VoiceSettings))]
internal sealed partial class VoiceJsonContext : JsonSerializerContext;

/// <summary>Reads and writes <see cref="VoiceSettings"/>: <c>voice.json</c> in the app's folder, the keys as secrets.</summary>
internal sealed class VoiceSettingsStore(IMobileStore? store)
{
    private const string FileName = "voice.json";
    private const string AliyunKeyName = "voice-aliyun-key";
    private const string VolcengineTokenName = "voice-volcengine-token";
    private const string OpenAiKeyName = "voice-openai-key";

    public async Task<VoiceSettings> LoadAsync()
    {
        if (store is null) return new VoiceSettings();
        VoiceSettings settings;
        try
        {
            var path = Path.Combine(store.DataDirectory, FileName);
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(path).ConfigureAwait(false), VoiceJsonContext.Default.VoiceSettings) ?? new VoiceSettings()
                : new VoiceSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file is not worth stopping the app for: voice input starts over from the defaults.
            settings = new VoiceSettings();
        }
        settings.AliyunApiKey = await store.GetSecretAsync(AliyunKeyName).ConfigureAwait(false) ?? "";
        settings.VolcengineAccessToken = await store.GetSecretAsync(VolcengineTokenName).ConfigureAwait(false) ?? "";
        settings.OpenAiApiKey = await store.GetSecretAsync(OpenAiKeyName).ConfigureAwait(false) ?? "";
        return settings;
    }

    public async Task SaveAsync(VoiceSettings settings)
    {
        if (store is null) return;
        Directory.CreateDirectory(store.DataDirectory);
        var path = Path.Combine(store.DataDirectory, FileName);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, VoiceJsonContext.Default.VoiceSettings)).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        await KeepAsync(store, AliyunKeyName, settings.AliyunApiKey).ConfigureAwait(false);
        await KeepAsync(store, VolcengineTokenName, settings.VolcengineAccessToken).ConfigureAwait(false);
        await KeepAsync(store, OpenAiKeyName, settings.OpenAiApiKey).ConfigureAwait(false);
    }

    // An empty key removes the secret, but only one that is there: a store may refuse to remove what it never had.
    private static async Task KeepAsync(IMobileStore store, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) await store.SetSecretAsync(name, value.Trim()).ConfigureAwait(false);
        else if (await store.GetSecretAsync(name).ConfigureAwait(false) is not null) await store.SetSecretAsync(name, null).ConfigureAwait(false);
    }
}
