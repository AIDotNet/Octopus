using System.Text.RegularExpressions;

namespace Octopus.App.Common;

/// <summary>
/// Resolves an AI model id (plus optional display label, description, and owning agent id) to its LobeHub icon key
/// in <see cref="ModelIconStore"/>, following the matching rules of <c>@lobehub/icons</c> plus Octopus agent aliases.
/// Returns <c>null</c> when no specific brand applies so <see cref="ModelIconView"/> renders the neutral sparkle mark.
/// </summary>
public static partial class ModelIcons
{
    private readonly record struct Rule(string IconKey, Regex Pattern, bool SafeForDescription = false);

    // Ordered from most-specific to broadest, matching @lobehub/icons modelConfig.ts precedence.
    private static readonly Rule[] Rules =
    [
        // OpenAI specialized models first
        new("sora", SoraRegex(), SafeForDescription: true),
        new("dalle", DalleRegex(), SafeForDescription: true),
        new("openai", OpenAiRegex(), SafeForDescription: true),

        // Zhipu / Z.ai / GLM / CodeGeeX
        new("glmv", GlmVRegex(), SafeForDescription: true),
        new("zai", ZaiGlmRegex(), SafeForDescription: true),
        new("chatglm", ChatGlmRegex(), SafeForDescription: true),
        new("codegeex", CodeGeeXRegex(), SafeForDescription: true),
        new("zhipu", ZhipuRegex(), SafeForDescription: true),

        // Anthropic / Claude (including Claude Code short aliases: opus, sonnet, haiku, fable, mythos)
        new("claude", ClaudeRegex(), SafeForDescription: true),
        new("anthropic", AnthropicRegex(), SafeForDescription: true),

        // Google / Gemini / Gemma / DeepMind / Vertex / PaLM / NanoBanana
        new("nanobanana", NanoBananaRegex(), SafeForDescription: true),
        new("gemini", GeminiRegex(), SafeForDescription: true),
        new("deepmind", ImagenRegex(), SafeForDescription: true),
        new("gemma", GemmaRegex(), SafeForDescription: true),
        new("vertexai", VeoRegex(), SafeForDescription: true),
        new("palm", PalmRegex(), SafeForDescription: true),

        // DeepSeek
        new("deepseek", DeepSeekRegex(), SafeForDescription: true),

        // xAI / Grok
        new("grok", GrokRegex(), SafeForDescription: true),
        new("xai", XaiRegex(), SafeForDescription: true),

        // Moonshot / Kimi
        new("kimi", KimiRegex(), SafeForDescription: true),
        new("moonshot", MoonshotRegex(), SafeForDescription: true),

        // Alibaba / Qwen
        new("qwen", QwenRegex(), SafeForDescription: true),

        // MiniMax
        new("minimax", MiniMaxRegex(), SafeForDescription: true),

        // Mistral
        new("mistral", MistralRegex(), SafeForDescription: true),

        // NVIDIA (before Meta/Llama so nvidia/llama-*-nemotron resolves to NVIDIA)
        new("nvidia", NvidiaRegex(), SafeForDescription: true),

        // Meta / Muse / Llama
        new("muse", MuseRegex(), SafeForDescription: true),
        new("meta", MetaLlamaRegex(), SafeForDescription: true),

        // ByteDance / Doubao
        new("doubao", DoubaoRegex(), SafeForDescription: true),
        new("bytedance", ByteDanceRegex(), SafeForDescription: true),

        // Xiaomi MiMo
        new("xiaomimimo", XiaomiMiMoRegex(), SafeForDescription: true),

        // Tencent Hunyuan
        new("hunyuan", HunyuanRegex(), SafeForDescription: true),

        // Baidu Wenxin / ERNIE
        new("wenxin", WenxinRegex(), SafeForDescription: true),
        new("baiducloud", BaiduRegex()),

        // Cohere / Aya
        new("aya", AyaRegex()),
        new("cohere", CohereRegex(), SafeForDescription: true),

        // Perplexity
        new("perplexity", PerplexityRegex(), SafeForDescription: true),

        // Microsoft / Phi
        new("microsoft", MicrosoftRegex(), SafeForDescription: true),

        // Amazon / AWS / Nova / Bedrock
        new("nova", NovaRegex()),
        new("bedrock", BedrockRegex(), SafeForDescription: true),
        new("aws", AwsRegex()),

        // Chinese & international model labs
        new("stepfun", StepFunRegex()),
        new("baichuan", BaichuanRegex(), SafeForDescription: true),
        new("yi", YiRegex()),
        new("internlm", InternLmRegex(), SafeForDescription: true),
        new("sensenova", SenseNovaRegex(), SafeForDescription: true),
        new("spark", SparkRegex()),
        new("longcat", LongCatRegex(), SafeForDescription: true),
        new("kwaipilot", KwaiPilotRegex()),
        new("ling", LingRegex()),
        new("skywork", SkyworkRegex(), SafeForDescription: true),
        new("tiangong", TiangongRegex(), SafeForDescription: true),

        // Open-weight, routing, coding & multimodal labs
        new("cursor", CursorModelRegex()),
        new("githubcopilot", CopilotRegex(), SafeForDescription: true),
        new("ollama", OllamaRegex()),
        new("openrouter", OpenRouterRegex()),
        new("flux", FluxRegex()),
        new("stability", StabilityRegex(), SafeForDescription: true),
        new("ibm", IbmRegex(), SafeForDescription: true),
        new("ai21", Ai21Regex(), SafeForDescription: true),
        new("upstage", UpstageRegex()),
        new("snowflake", SnowflakeRegex(), SafeForDescription: true),
        new("dbrx", DbrxRegex(), SafeForDescription: true),
        new("liquid", LiquidRegex()),
        new("ai2", OlmoRegex()),
        new("reka", RekaRegex()),
        new("inflection", InflectionRegex(), SafeForDescription: true),
        new("voyage", VoyageRegex(), SafeForDescription: true),
        new("jina", JinaRegex()),
        new("huggingface", HuggingFaceRegex()),
        new("kling", KlingRegex(), SafeForDescription: true),
        new("suno", SunoRegex()),
        new("v0", V0Regex()),
        new("morph", MorphRegex()),
        new("menlo", MenloRegex()),

        // Broad fallbacks (Google, Alibaba) kept near the bottom so specific model families win first.
        new("google", GoogleRegex()),
        new("alibaba", AlibabaRegex()),
    ];

    /// <summary>
    /// Resolves a model option to a bundled LobeHub icon key, or <c>null</c> for neutral/unknown models.
    /// </summary>
    public static string? KeyFor(
        string? modelId,
        string? label = null,
        string? description = null,
        string? fallbackAgentId = null)
    {
        if (Match(modelId, descriptionOnly: false) is { } byId)
        {
            return byId;
        }

        if (Match(label, descriptionOnly: false) is { } byLabel)
        {
            return byLabel;
        }

        // Descriptions often identify the backing model on "Default (recommended)" rows (e.g. "Opus 5.5"),
        // so match only high-confidence brand/model rules to avoid false positives on prose descriptions.
        if (Match(description, descriptionOnly: true) is { } byDesc)
        {
            return byDesc;
        }

        // Agent-specific short model ids like Gemini CLI's "pro", "flash", "flash-lite".
        if (!string.IsNullOrWhiteSpace(modelId) && !string.IsNullOrWhiteSpace(fallbackAgentId))
        {
            var id = modelId.Trim();
            if (!IsNeutralPlaceholder(id))
            {
                return AgentFallbackIcon(fallbackAgentId, id);
            }
        }

        return null;
    }

    private static string? Match(string? text, bool descriptionOnly)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var candidate = text.Trim();
        foreach (var rule in Rules)
        {
            if (descriptionOnly && !rule.SafeForDescription) continue;
            if (rule.Pattern.IsMatch(candidate))
            {
                return rule.IconKey;
            }
        }

        return null;
    }

    private static bool IsNeutralPlaceholder(string id) =>
        id.Equals("default", StringComparison.OrdinalIgnoreCase)
        || id.Equals("auto", StringComparison.OrdinalIgnoreCase);

    private static string? AgentFallbackIcon(string agentId, string modelId)
    {
        if (agentId.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            && (modelId.Equals("pro", StringComparison.OrdinalIgnoreCase)
                || modelId.Equals("flash", StringComparison.OrdinalIgnoreCase)
                || modelId.StartsWith("flash-", StringComparison.OrdinalIgnoreCase)))
        {
            return "gemini";
        }

        return null;
    }

    [GeneratedRegex(@"(?:^|[/\-_\s])sora(?:$|[/\-_\s\d])", RegexOptions.IgnoreCase)]
    private static partial Regex SoraRegex();

    [GeneratedRegex(@"dall-?e", RegexOptions.IgnoreCase)]
    private static partial Regex DalleRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:gpt[-_\s]?\d|gpt-?oss|chatgpt|o[134](?:$|[-_\s.])|codex|openai|davinci|babbage|whisper-|tts-|text-embedding-|omni-moderation|text-moderation|computer-use)", RegexOptions.IgnoreCase)]
    private static partial Regex OpenAiRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])glm-?[\d.]+v\b", RegexOptions.IgnoreCase)]
    private static partial Regex GlmVRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:glm-?[45]|z-?ai(?:/|$))", RegexOptions.IgnoreCase)]
    private static partial Regex ZaiGlmRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:chatglm|glm-)", RegexOptions.IgnoreCase)]
    private static partial Regex ChatGlmRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])codegeex", RegexOptions.IgnoreCase)]
    private static partial Regex CodeGeeXRegex();

    [GeneratedRegex(@"zhipu", RegexOptions.IgnoreCase)]
    private static partial Regex ZhipuRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:claude|(?:opus|sonnet|haiku|fable|mythos)(?:$|[-_\s\d]))", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudeRegex();

    [GeneratedRegex(@"anthropic", RegexOptions.IgnoreCase)]
    private static partial Regex AnthropicRegex();

    [GeneratedRegex(@"nano-?banana|gemini-[\d.]+-(?:flash(?:-lite)?|pro)-image", RegexOptions.IgnoreCase)]
    private static partial Regex NanoBananaRegex();

    [GeneratedRegex(@"gemini", RegexOptions.IgnoreCase)]
    private static partial Regex GeminiRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])imagen[\-\d]", RegexOptions.IgnoreCase)]
    private static partial Regex ImagenRegex();

    [GeneratedRegex(@"gemma", RegexOptions.IgnoreCase)]
    private static partial Regex GemmaRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])veo[\-\d]", RegexOptions.IgnoreCase)]
    private static partial Regex VeoRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])palm(?:$|[-_\s\d])", RegexOptions.IgnoreCase)]
    private static partial Regex PalmRegex();

    [GeneratedRegex(@"deepseek", RegexOptions.IgnoreCase)]
    private static partial Regex DeepSeekRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])grok(?:$|[-_\s\d])", RegexOptions.IgnoreCase)]
    private static partial Regex GrokRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])x-?ai(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex XaiRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])kimi(?:$|[-_\s\d])", RegexOptions.IgnoreCase)]
    private static partial Regex KimiRegex();

    [GeneratedRegex(@"moonshot", RegexOptions.IgnoreCase)]
    private static partial Regex MoonshotRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:qwen|qwq|qvq|tongyi|wanx|wan\d)", RegexOptions.IgnoreCase)]
    private static partial Regex QwenRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:minimax|abab)", RegexOptions.IgnoreCase)]
    private static partial Regex MiniMaxRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:mistral|mixtral|codestral|mathstral|pixtral|ministral|magistral|devstral|voxtral)", RegexOptions.IgnoreCase)]
    private static partial Regex MistralRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])muse-(?:spark|wonder|image|video)", RegexOptions.IgnoreCase)]
    private static partial Regex MuseRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:meta-?llama|llama|meta/)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaLlamaRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:doubao|ep-\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DoubaoRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:bytedance|skylark|seed-(?:thinking|coder|pro|oss|tts|icl|\d))", RegexOptions.IgnoreCase)]
    private static partial Regex ByteDanceRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:xiaomi/)?mimo(?:$|[-_\s\d])", RegexOptions.IgnoreCase)]
    private static partial Regex XiaomiMiMoRegex();

    [GeneratedRegex(@"hunyuan", RegexOptions.IgnoreCase)]
    private static partial Regex HunyuanRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:ernie|wenxin)", RegexOptions.IgnoreCase)]
    private static partial Regex WenxinRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:baidu|qianfan)", RegexOptions.IgnoreCase)]
    private static partial Regex BaiduRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])aya(?:$|[-_\s\d])", RegexOptions.IgnoreCase)]
    private static partial Regex AyaRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:cohere|command-(?:r|a|light|nightly))", RegexOptions.IgnoreCase)]
    private static partial Regex CohereRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:perplexity|pplx|sonar(?:$|[-_\s]))", RegexOptions.IgnoreCase)]
    private static partial Regex PerplexityRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:nvidia|nemotron|nemoretriever|openreasoning|nv-)", RegexOptions.IgnoreCase)]
    private static partial Regex NvidiaRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:microsoft|phi-\d|wizardlm|mai-)", RegexOptions.IgnoreCase)]
    private static partial Regex MicrosoftRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])nova-(?:pro|lite|micro|premier|canvas|reel|sonic)", RegexOptions.IgnoreCase)]
    private static partial Regex NovaRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:bedrock|amazon\.titan|titan-)", RegexOptions.IgnoreCase)]
    private static partial Regex BedrockRegex();

    [GeneratedRegex(@"(?:^|/)aws(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AwsRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:stepfun|step-\d)", RegexOptions.IgnoreCase)]
    private static partial Regex StepFunRegex();

    [GeneratedRegex(@"baichuan", RegexOptions.IgnoreCase)]
    private static partial Regex BaichuanRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])yi-(?:large|medium|spark|vision|lightning|coder|\d)", RegexOptions.IgnoreCase)]
    private static partial Regex YiRegex();

    [GeneratedRegex(@"intern(?:lm|vl)", RegexOptions.IgnoreCase)]
    private static partial Regex InternLmRegex();

    [GeneratedRegex(@"sense(?:chat|nova)", RegexOptions.IgnoreCase)]
    private static partial Regex SenseNovaRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:spark[-_\s]?(?:max|pro|lite|ultra|v?\d)|generalv3)", RegexOptions.IgnoreCase)]
    private static partial Regex SparkRegex();

    [GeneratedRegex(@"longcat", RegexOptions.IgnoreCase)]
    private static partial Regex LongCatRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:kwaipilot|kat-)", RegexOptions.IgnoreCase)]
    private static partial Regex KwaiPilotRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:ling-|ring-)", RegexOptions.IgnoreCase)]
    private static partial Regex LingRegex();

    [GeneratedRegex(@"skywork", RegexOptions.IgnoreCase)]
    private static partial Regex SkyworkRegex();

    [GeneratedRegex(@"tiangong", RegexOptions.IgnoreCase)]
    private static partial Regex TiangongRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:composer(?:$|[-_\s\d])|cursor-)", RegexOptions.IgnoreCase)]
    private static partial Regex CursorModelRegex();

    [GeneratedRegex(@"copilot", RegexOptions.IgnoreCase)]
    private static partial Regex CopilotRegex();

    [GeneratedRegex(@"(?:^|/)ollama(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex OllamaRegex();

    [GeneratedRegex(@"^openrouter(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex OpenRouterRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])flux(?:$|[-_\s.\d])", RegexOptions.IgnoreCase)]
    private static partial Regex FluxRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:stable-diffusion|stable-video|stablelm|sdxl|sd3)", RegexOptions.IgnoreCase)]
    private static partial Regex StabilityRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:ibm/|granite-)", RegexOptions.IgnoreCase)]
    private static partial Regex IbmRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:jamba|ai21)", RegexOptions.IgnoreCase)]
    private static partial Regex Ai21Regex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:upstage|solar-)", RegexOptions.IgnoreCase)]
    private static partial Regex UpstageRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:snowflake|arctic)", RegexOptions.IgnoreCase)]
    private static partial Regex SnowflakeRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])dbrx", RegexOptions.IgnoreCase)]
    private static partial Regex DbrxRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:liquid|lfm-)", RegexOptions.IgnoreCase)]
    private static partial Regex LiquidRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])olmo-", RegexOptions.IgnoreCase)]
    private static partial Regex OlmoRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])reka-", RegexOptions.IgnoreCase)]
    private static partial Regex RekaRegex();

    [GeneratedRegex(@"inflection-", RegexOptions.IgnoreCase)]
    private static partial Regex InflectionRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])voyage-", RegexOptions.IgnoreCase)]
    private static partial Regex VoyageRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])jina-", RegexOptions.IgnoreCase)]
    private static partial Regex JinaRegex();

    [GeneratedRegex(@"huggingface|hf\.co/", RegexOptions.IgnoreCase)]
    private static partial Regex HuggingFaceRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])kling(?:$|[-_\s\d]|ai)", RegexOptions.IgnoreCase)]
    private static partial Regex KlingRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])suno(?:$|[-_\s\d])", RegexOptions.IgnoreCase)]
    private static partial Regex SunoRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])v0-", RegexOptions.IgnoreCase)]
    private static partial Regex V0Regex();

    [GeneratedRegex(@"(?:^|[/\-_\s])morph-", RegexOptions.IgnoreCase)]
    private static partial Regex MorphRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:menlo|jan-nano)", RegexOptions.IgnoreCase)]
    private static partial Regex MenloRegex();

    [GeneratedRegex(@"(?:^|[/\-_\s])(?:google/|learnlm)", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleRegex();

    [GeneratedRegex(@"(?:^|/)alibaba(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AlibabaRegex();
}
