using System.Text.Json.Serialization;

namespace Octopus.Core.Agents;

/// <summary>
/// What an agent CLI is and what can be configured on it. Data, not code: built-in definitions are JSON files and a
/// user can add their own. A capability block that is null means the agent does not support it, and the UI shows no
/// control for it. See design/05-智能体设计.md.
/// </summary>
public sealed class AgentDefinition
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Key of a built-in icon; null falls back to the first letter of the name.</summary>
    public string? Icon { get; set; }
    public string? Homepage { get; set; }
    /// <summary>Whether the arguments were checked against the real CLI.</summary>
    public bool Verified { get; set; }
    public AgentDetect Detect { get; set; } = new();
    public AgentCapabilities Capabilities { get; set; } = new();

    /// <summary>True for a definition loaded from the user's agents folder.</summary>
    [JsonIgnore]
    public bool IsCustom { get; set; }
}

public sealed class AgentDetect
{
    /// <summary>Executable names to look for on PATH, in order.</summary>
    public List<string> Executables { get; set; } = [];
    public List<string> VersionArgs { get; set; } = ["--version"];
    /// <summary>Regex whose first group is the version.</summary>
    public string? VersionRegex { get; set; }
    /// <summary>The version output must contain this; rules out another program with the same name.</summary>
    public string? Fingerprint { get; set; }
    public Dictionary<string, string> ProbeEnv { get; set; } = [];
    public List<AgentInstallHint> InstallHints { get; set; } = [];
}

public sealed class AgentInstallHint
{
    public string Kind { get; set; } = "";
    public string Command { get; set; } = "";
}

public sealed class AgentCapabilities
{
    /// <summary>Arguments always passed, before everything else.</summary>
    public List<string> BaseArgs { get; set; } = [];
    public Dictionary<string, string> BaseEnv { get; set; } = [];
    public ModelCapability? Model { get; set; }
    public EffortCapability? Effort { get; set; }
    public FastCapability? Fast { get; set; }
    public PermissionCapability? Permission { get; set; }
    public PromptCapability? Prompt { get; set; }
    public ResumeCapability? Resume { get; set; }
    public bool ExtraArgs { get; set; } = true;
    public EnvCapability? Env { get; set; }
    public List<AgentFeature> Features { get; set; } = [];
}

/// <summary>A selectable value. <see cref="Args"/> replaces the block's argument template for this value when set.</summary>
public sealed class AgentOption
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
    public string? Note { get; set; }
    /// <summary>Interface text per language (BCP 47 tag, e.g. <c>zh-CN</c>); see <see cref="LocalizedTextExtensions"/>.</summary>
    public Dictionary<string, LocalizedText>? Localized { get; set; }
    public List<string>? Args { get; set; }

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrEmpty(Label) ? Id : Label!;
}

public sealed class ModelCapability
{
    /// <summary>Argument template; <c>{value}</c> is the model id (with any model params appended).</summary>
    public List<string> Args { get; set; } = [];
    public bool AllowCustom { get; set; } = true;
    public List<AgentOption> Options { get; set; } = [];
    public ModelDiscoverySpec? Discover { get; set; }
}

public sealed class ModelDiscoverySpec
{
    /// <summary>Arguments that make the CLI print its models, e.g. <c>["models"]</c>.</summary>
    public List<string> Args { get; set; } = [];
    /// <summary><c>lines</c>: one id per line. <c>idDashLabel</c>: <c>id - label</c> per line.</summary>
    public string Parser { get; set; } = "lines";
}

public sealed class EffortCapability
{
    /// <summary>The name the agent uses, e.g. "Effort" or "Reasoning effort".</summary>
    public string? Label { get; set; }
    /// <summary>Argument template with <c>{value}</c>. Empty when the effort is carried by <see cref="ModelParam"/>.</summary>
    public List<string> Args { get; set; } = [];
    /// <summary>
    /// For CLIs that encode effort in the model id: a fragment such as <c>effort={value}</c>, appended to the model as
    /// <c>model[effort=high,fast=true]</c>.
    /// </summary>
    public string? ModelParam { get; set; }
    public List<AgentOption> Options { get; set; } = [];
    /// <summary>Model id (or <c>*</c>) to the effort ids that model supports; a model not listed supports all.</summary>
    public Dictionary<string, List<string>>? PerModel { get; set; }
    /// <summary>Interface text per language (BCP 47 tag, e.g. <c>zh-CN</c>); see <see cref="LocalizedTextExtensions"/>.</summary>
    public Dictionary<string, LocalizedText>? Localized { get; set; }
}

public sealed class FastCapability
{
    public List<string> Args { get; set; } = [];
    /// <summary>See <see cref="EffortCapability.ModelParam"/>; e.g. <c>fast=true</c>.</summary>
    public string? ModelParam { get; set; }
}

public sealed class PermissionCapability
{
    public string Default { get; set; } = PermissionModes.Default;
    /// <summary>Unified mode id (<see cref="PermissionModes"/>) to its arguments. Sparse: only what the agent supports.</summary>
    public Dictionary<string, PermissionModeSpec> Modes { get; set; } = [];
}

public sealed class PermissionModeSpec
{
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string>? Env { get; set; }
    public bool Danger { get; set; }
    /// <summary>Overrides the unified label when the agent's own wording differs.</summary>
    public string? Label { get; set; }
    /// <summary>Interface text per language (BCP 47 tag, e.g. <c>zh-CN</c>); see <see cref="LocalizedTextExtensions"/>.</summary>
    public Dictionary<string, LocalizedText>? Localized { get; set; }
}

/// <summary>The unified permission levels. An agent declares the subset it supports.</summary>
public static class PermissionModes
{
    public const string Default = "default";
    public const string AcceptEdits = "acceptEdits";
    public const string Plan = "plan";
    public const string Auto = "auto";
    public const string DontAsk = "dontAsk";
    public const string Full = "full";

    public static IReadOnlyList<string> All { get; } = [Default, AcceptEdits, Plan, Auto, DontAsk, Full];
}

public sealed class PromptCapability
{
    /// <summary><c>positional</c>: the prompt is the last argument. <c>flag</c>: <see cref="Args"/> with <c>{value}</c>.</summary>
    public string Mode { get; set; } = "positional";
    public List<string>? Args { get; set; }
    /// <summary>Positional only: put <c>--</c> before the prompt.</summary>
    public bool Separator { get; set; }
}

public sealed class ResumeCapability
{
    /// <summary>Arguments that continue the most recent session in the working directory.</summary>
    public List<string>? ContinueLatest { get; set; }
}

public sealed class EnvCapability
{
    public List<KnownEnvVar> Known { get; set; } = [];
    public bool AllowCustom { get; set; } = true;
}

public sealed class KnownEnvVar
{
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public string? Description { get; set; }
    public bool Secret { get; set; }
    /// <summary>Interface text per language (BCP 47 tag, e.g. <c>zh-CN</c>); see <see cref="LocalizedTextExtensions"/>.</summary>
    public Dictionary<string, LocalizedText>? Localized { get; set; }
}

/// <summary>An agent-specific setting, rendered by its <see cref="Type"/>.</summary>
public sealed class AgentFeature
{
    public string Id { get; set; } = "";
    /// <summary><c>string</c>, <c>text</c>, <c>bool</c>, <c>enum</c>, <c>file</c>, <c>directory</c> or <c>list</c>.</summary>
    public string Type { get; set; } = "string";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public string? Placeholder { get; set; }
    /// <summary>Argument template with <c>{value}</c>; for <c>list</c> it repeats once per item. May be empty when only <see cref="Env"/> is set.</summary>
    public List<string> Args { get; set; } = [];
    /// <summary>Environment variable set to the value instead of (or in addition to) arguments.</summary>
    public string? Env { get; set; }
    /// <summary><c>enum</c>: the choices.</summary>
    public List<AgentOption>? Options { get; set; }
    /// <summary>Interface text per language (BCP 47 tag, e.g. <c>zh-CN</c>); see <see cref="LocalizedTextExtensions"/>.</summary>
    public Dictionary<string, LocalizedText>? Localized { get; set; }
}
