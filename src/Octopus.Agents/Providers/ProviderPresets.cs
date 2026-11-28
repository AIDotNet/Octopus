using Octopus.Core.Agents;

namespace Octopus.Agents.Providers;

/// <summary>Built-in quick provider presets for Settings Scheme B.</summary>
public static class ProviderPresets
{
    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new(
            Id: "deepseek-anthropic",
            Name: "DeepSeek (Anthropic)",
            NameZh: "DeepSeek (Anthropic 兼容)",
            Protocol: ProviderProtocol.Anthropic,
            BaseUrl: "https://api.deepseek.com/anthropic",
            AuthEnvPreference: "ANTHROPIC_AUTH_TOKEN",
            DefaultModels:
            [
                new ManagedModelSpec { Id = "deepseek-reasoner", DisplayName = "DeepSeek Reasoner", Enabled = true, Tier = ModelSlotTier.Flagship, SupportedEfforts = ["low", "medium", "high"] },
                new ManagedModelSpec { Id = "deepseek-chat", DisplayName = "DeepSeek Chat", Enabled = true, Tier = ModelSlotTier.Fast, SupportedEfforts = ["low", "medium"] },
            ]),
        new(
            Id: "openrouter",
            Name: "OpenRouter (OpenAI)",
            NameZh: "OpenRouter (OpenAI 兼容)",
            Protocol: ProviderProtocol.OpenAIChat,
            BaseUrl: "https://openrouter.ai/api/v1",
            AuthEnvPreference: null,
            DefaultModels:
            [
                new ManagedModelSpec { Id = "anthropic/claude-sonnet-4.6", DisplayName = "Claude Sonnet 4.6", Enabled = true, Tier = ModelSlotTier.Balanced, SupportedEfforts = ["low", "medium", "high"] },
                new ManagedModelSpec { Id = "deepseek/deepseek-r1", DisplayName = "DeepSeek R1", Enabled = true, Tier = ModelSlotTier.Flagship, SupportedEfforts = ["medium", "high"] },
                new ManagedModelSpec { Id = "qwen/qwen3-coder", DisplayName = "Qwen3 Coder", Enabled = true, Tier = ModelSlotTier.Fast, SupportedEfforts = ["low", "medium"] },
            ]),
        new(
            Id: "zhipu-anthropic",
            Name: "Zhipu GLM (Anthropic)",
            NameZh: "智谱 GLM (Anthropic 兼容)",
            Protocol: ProviderProtocol.Anthropic,
            BaseUrl: "https://open.bigmodel.cn/api/anthropic",
            AuthEnvPreference: "ANTHROPIC_AUTH_TOKEN",
            DefaultModels:
            [
                new ManagedModelSpec { Id = "glm-4.7", DisplayName = "GLM-4.7", Enabled = true, Tier = ModelSlotTier.Flagship, SupportedEfforts = ["low", "medium", "high"] },
                new ManagedModelSpec { Id = "glm-4.5-air", DisplayName = "GLM-4.5 Air", Enabled = true, Tier = ModelSlotTier.Fast, SupportedEfforts = ["low"] },
            ]),
        new(
            Id: "kimi-anthropic",
            Name: "Kimi (Anthropic)",
            NameZh: "Kimi (Anthropic 兼容)",
            Protocol: ProviderProtocol.Anthropic,
            BaseUrl: "https://api.moonshot.cn/anthropic",
            AuthEnvPreference: "ANTHROPIC_AUTH_TOKEN",
            DefaultModels:
            [
                new ManagedModelSpec { Id = "kimi-k2.5", DisplayName = "Kimi K2.5", Enabled = true, Tier = ModelSlotTier.Flagship, SupportedEfforts = ["low", "medium", "high"] },
            ]),
        new(
            Id: "ollama-local",
            Name: "Local Ollama",
            NameZh: "本地 Ollama",
            Protocol: ProviderProtocol.OpenAIChat,
            BaseUrl: "http://127.0.0.1:11434/v1",
            AuthEnvPreference: null,
            DefaultModels:
            [
                new ManagedModelSpec { Id = "qwen3-coder:32b", DisplayName = "Qwen3 Coder 32B", Enabled = true, Tier = ModelSlotTier.Balanced, SupportedEfforts = ["low", "medium"] },
            ]),
        new(
            Id: "google-ai-studio",
            Name: "Google AI Studio Key",
            NameZh: "Google AI Studio 官方 Key",
            Protocol: ProviderProtocol.GeminiOfficialKey,
            BaseUrl: "https://generativelanguage.googleapis.com",
            AuthEnvPreference: null,
            DefaultModels:
            [
                new ManagedModelSpec { Id = "gemini-3-pro-preview", DisplayName = "Gemini 3 Pro", Enabled = true, Tier = ModelSlotTier.Flagship },
                new ManagedModelSpec { Id = "gemini-3-flash-preview", DisplayName = "Gemini 3 Flash", Enabled = true, Tier = ModelSlotTier.Fast },
            ]),
        new(
            Id: "custom-anthropic",
            Name: "Custom Anthropic Gateway",
            NameZh: "自定义 Anthropic 网关",
            Protocol: ProviderProtocol.Anthropic,
            BaseUrl: "https://api.anthropic.com",
            AuthEnvPreference: "ANTHROPIC_AUTH_TOKEN",
            DefaultModels:
            [
                new ManagedModelSpec { Id = "claude-opus-5-5", DisplayName = "Claude Opus 5.5", Enabled = true, Tier = ModelSlotTier.Flagship, SupportedEfforts = ["low", "medium", "high", "xhigh", "max"] },
                new ManagedModelSpec { Id = "claude-sonnet-5-5", DisplayName = "Claude Sonnet 5.5", Enabled = true, Tier = ModelSlotTier.Balanced, SupportedEfforts = ["low", "medium", "high", "xhigh"] },
                new ManagedModelSpec { Id = "claude-haiku-5-5", DisplayName = "Claude Haiku 5.5", Enabled = true, Tier = ModelSlotTier.Fast, SupportedEfforts = ["low", "medium", "high"] },
            ]),
        new(
            Id: "custom-openai-responses",
            Name: "Custom OpenAI Responses",
            NameZh: "自定义 OpenAI (Responses)",
            Protocol: ProviderProtocol.OpenAIResponses,
            BaseUrl: "https://api.openai.com/v1",
            AuthEnvPreference: null,
            DefaultModels:
            [
                new ManagedModelSpec { Id = "gpt-6.2-astra", DisplayName = "GPT-6.2 Astra", Enabled = true, Tier = ModelSlotTier.Flagship, SupportedEfforts = ["low", "medium", "high", "xhigh", "max"] },
                new ManagedModelSpec { Id = "gpt-5.6-luna", DisplayName = "GPT-5.6 Luna", Enabled = true, Tier = ModelSlotTier.Balanced, SupportedEfforts = ["low", "medium", "high"] },
            ]),
    ];

    public static ModelProviderRecord CreateRecord(ProviderPreset preset, bool preferChinese = false) => new()
    {
        Id = "",
        Name = preferChinese ? preset.NameZh : preset.Name,
        Protocol = preset.Protocol,
        BaseUrl = preset.BaseUrl,
        AuthEnvPreference = preset.AuthEnvPreference,
        Enabled = true,
        IsolateConfigDir = true,
        AllowRemoteTunnel = true,
        Models = preset.DefaultModels.Select(m => new ManagedModelSpec
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Enabled = m.Enabled,
            Tier = m.Tier,
            SupportedEfforts = m.SupportedEfforts is { Count: > 0 } ? [.. m.SupportedEfforts] : null,
            ContextWindow = m.ContextWindow,
        }).ToList(),
    };
}
