using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Octopus.Acp.Dialects;
using Octopus.Acp.Protocol;
using Octopus.Agents;
using Octopus.App.AgentGui;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Launcher;
using Octopus.Core.Agents;
using Xunit;

namespace Octopus.App.Tests;

public class ModelIconsTests
{
    [AvaloniaFact]
    public void Every_bundled_model_icon_parses_into_valid_geometry_shapes()
    {
        var store = ModelIconStore.Instance;
        var ids = store.AllIconIds.ToList();
        Assert.NotEmpty(ids);

        foreach (var id in ids)
        {
            var graphic = store.Get(id);
            Assert.NotNull(graphic);
            Assert.NotEmpty(graphic!.Shapes);
            Assert.True(graphic.ViewBox.Width > 0 && graphic.ViewBox.Height > 0, $"Invalid viewBox for '{id}'");
        }
    }

    [Theory]
    // Screenshot models & Claude Code aliases
    [InlineData("deepseek-v4.1-flash-1017[1m]", null, null, null, "deepseek")]
    [InlineData("claude-opus-5-5", null, null, null, "claude")]
    [InlineData("claude-opus-5.5", null, null, null, "claude")]
    [InlineData("gemini-4-argon", null, null, null, "gemini")]
    [InlineData("gemini-4-carbon", null, null, null, "gemini")]
    [InlineData("gemini-4-hydrogen", null, null, null, "gemini")]
    [InlineData("default", "Default (recommended)", "Opus 5.5", "claude", "claude")]
    [InlineData("opus", "Opus 5.5", "For complex work and everyday tasks", "claude", "claude")]
    [InlineData("fable", "Fable 5.2", "For your toughest challenges", "claude", "claude")]
    [InlineData("sonnet", "Sonnet 5.5", null, "claude", "claude")]
    [InlineData("haiku", "Haiku 5.5", null, "claude", "claude")]
    // OpenAI
    [InlineData("gpt-6.2-astra", null, null, "codex", "openai")]
    [InlineData("gpt-oss-120b-medium", "GPT-OSS 120B (Medium)", null, "agy", "openai")]
    [InlineData("o3-mini", null, null, null, "openai")]
    [InlineData("openai/o4-mini", null, null, null, "openai")]
    [InlineData("gpt-5.3-codex", null, null, "cursor", "openai")]
    [InlineData("sora-2", null, null, null, "sora")]
    [InlineData("dall-e-3", null, null, null, "dalle")]
    // Google / Gemini / Gemma
    [InlineData("gemini-pro-orchestrator-neon", "Orchestrator (Neon)", null, "agy", "gemini")]
    [InlineData("gemma-3-27b", null, null, null, "gemma")]
    [InlineData("nano-banana-pro", null, null, null, "nanobanana")]
    [InlineData("pro", "Pro", null, "gemini", "gemini")]
    [InlineData("flash", "Flash", null, "gemini", "gemini")]
    [InlineData("flash-lite", "Flash Lite", null, "gemini", "gemini")]
    // Other major AI labs
    [InlineData("grok-4.7", null, null, "grok", "grok")]
    [InlineData("qwen3-coder-plus", null, null, "qwen", "qwen")]
    [InlineData("qwq-32b", null, null, null, "qwen")]
    [InlineData("kimi-k2.5", null, null, "kimi", "kimi")]
    [InlineData("moonshot-v1-128k", null, null, null, "moonshot")]
    [InlineData("glm-5", null, null, null, "zai")]
    [InlineData("glm-4.5v", null, null, null, "glmv")]
    [InlineData("chatglm3-6b", null, null, null, "chatglm")]
    [InlineData("minimax-m2", null, null, "minimax", "minimax")]
    [InlineData("meta-llama/llama-4-maverick", null, null, null, "meta")]
    [InlineData("mistral-large-latest", null, null, null, "mistral")]
    [InlineData("devstral-small", null, null, null, "mistral")]
    [InlineData("doubao-1.5-pro", null, null, null, "doubao")]
    [InlineData("mimo-v2-flash", null, null, "mimo", "xiaomimimo")]
    [InlineData("hunyuan-turbos", null, null, null, "hunyuan")]
    [InlineData("ernie-4.5", null, null, null, "wenxin")]
    [InlineData("command-r-plus", null, null, null, "cohere")]
    [InlineData("sonar-pro", null, null, null, "perplexity")]
    [InlineData("nvidia/llama-3.1-nemotron-70b", null, null, null, "nvidia")]
    [InlineData("phi-4", null, null, null, "microsoft")]
    [InlineData("amazon/nova-pro-v1", null, null, null, "nova")]
    [InlineData("composer-2.5", null, null, "cursor", "cursor")]
    [InlineData("step-2-16k", null, null, null, "stepfun")]
    // Neutral / unknown models fall back cleanly without false positives on prose descriptions
    [InlineData(null, "Agent default", null, "claude", null)]
    [InlineData("auto", "Auto", null, "cursor", null)]
    [InlineData("my-local-model", "Custom Model", "For everyday command line step spark tasks", null, null)]
    public void Resolves_model_identifiers_and_aliases_to_bundled_lobehub_icons(
        string? modelId,
        string? label,
        string? description,
        string? agentId,
        string? expected)
    {
        var key = ModelIcons.KeyFor(modelId, label, description, agentId);
        Assert.Equal(expected, key);
        if (expected is not null)
        {
            Assert.True(ModelIconStore.Instance.Contains(expected), $"Missing bundled icon '{expected}'");
        }
    }

    [AvaloniaFact]
    public void ModelIconView_renders_solid_gradient_mono_and_fallback_icons_across_themes()
    {
        var icon = new ModelIconView { IconKey = "claude", Size = 15 };
        var window = new Window { Width = 64, Height = 64, Content = icon };
        window.Show();
        Frames.Pump();

        Assert.Equal(15, icon.DesiredSize.Width);
        Assert.Equal(15, icon.DesiredSize.Height);

        foreach (var key in new string?[] { "gemini", "nova", "meta", "minimax", "openai", "deepseek", "grok", "zai", null, "unknown-key" })
        {
            icon.IconKey = key;
            Frames.Pump();
        }

        window.RequestedThemeVariant = ThemeVariant.Dark;
        icon.IconKey = "openai";
        Frames.Pump();
        icon.IconKey = null;
        Frames.Pump();

        window.Close();
    }

    [AvaloniaFact]
    public void Composer_populates_model_icon_keys_and_updates_selected_model_icon()
    {
        var catalog = AppHost.Get<IAgentCatalog>();
        var dialects = new AcpDialectRegistry();
        var composer = new AgentComposerCapabilities(
            catalog.Find("claude")!,
            dialects.Resolve("claude-acp"),
            PermissionModes.Default,
            null, null, null);

        composer.UpdateFromSession(
            new AcpInitializeResult(),
            modes: null,
            models: new AcpSessionModelState
            {
                CurrentModelId = "default",
                AvailableModels =
                [
                    new AcpModelInfo { ModelId = "default", Name = "Default (recommended)", Description = "Opus 5.5" },
                    new AcpModelInfo { ModelId = "deepseek-v4.1-flash-1017[1m]", Name = "deepseek-v4.1-flash-1017[1m]" },
                    new AcpModelInfo { ModelId = "gemini-4-argon", Name = "gemini-4-argon" },
                ],
            },
            configOptions: [],
            commands: []);

        Assert.Equal("claude", composer.SelectedModelIconKey);
        Assert.Equal("claude", composer.ModelChoices[0].ModelIconKey);
        Assert.Equal("deepseek", composer.ModelChoices[1].ModelIconKey);
        Assert.Equal("gemini", composer.ModelChoices[2].ModelIconKey);

        composer.SelectedModelId = "deepseek-v4.1-flash-1017[1m]";
        Assert.Equal("deepseek", composer.SelectedModelIconKey);

        composer.SelectedModelId = "gemini-4-argon";
        Assert.Equal("gemini", composer.SelectedModelIconKey);
    }

    [AvaloniaFact]
    public void Launcher_model_options_populate_icon_keys()
    {
        var catalog = AppHost.Get<IAgentCatalog>();
        using var vm = new AgentLaunchViewModel(
            catalog.Find("agy")!,
            new AgentProfile(),
            PermissionModes.Default,
            AppHost.Get<IAgentCommandCompiler>(),
            AppHost.Get<IModelDiscovery>(),
            executable: null,
            launch: _ => { });

        // First row is "Agent default" (neutral sparkle fallback -> null icon key).
        Assert.Null(vm.Models[0].IconKey);
        Assert.Contains(vm.Models, m => m.Id is not null && m.Id.StartsWith("gemini-") && m.IconKey == "gemini");
        Assert.Contains(vm.Models, m => m.Id is not null && m.Id.StartsWith("claude-") && m.IconKey == "claude");
        Assert.Contains(vm.Models, m => m.Id is not null && m.Id.StartsWith("gpt-") && m.IconKey == "openai");
    }
}
