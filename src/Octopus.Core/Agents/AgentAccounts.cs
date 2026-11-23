using System.Text.Json.Serialization;

namespace Octopus.Core.Agents;

/// <summary>
/// How an agent CLI isolates or switches subscription accounts (design/13-多账户切换.md §3).
/// Null on <see cref="AgentDefinition.Accounts"/> means the agent does not expose multi-account management.
/// </summary>
public sealed class AgentAccountsCapability
{
    /// <summary><see cref="AccountModes.EnvDir"/> or <see cref="AccountModes.GlobalKeyring"/>.</summary>
    public string Mode { get; set; } = AccountModes.EnvDir;

    /// <summary>For <c>envDir</c>: environment variable pointing to the isolated config directory (e.g. <c>CLAUDE_CONFIG_DIR</c>).</summary>
    public string? Env { get; set; }

    /// <summary>For <c>envDir</c>: default directory used when <see cref="Env"/> is unset (e.g. <c>~/.claude</c>).</summary>
    public string? DefaultDir { get; set; }

    /// <summary>Arguments passed to the agent executable to sign in interactively in an embedded terminal.</summary>
    public List<string> LoginArgs { get; set; } = [];

    /// <summary>Arguments passed to the agent executable to sign out, when supported.</summary>
    public List<string>? LogoutArgs { get; set; }

    /// <summary>
    /// Environment variables from the host/shell that would override subscription OAuth (e.g. <c>ANTHROPIC_API_KEY</c>,
    /// <c>OPENAI_API_KEY</c>, <c>GH_TOKEN</c>) and are unset unless explicitly set in the user's profile or launch env.
    /// </summary>
    public List<string> UnsetEnv { get; set; } = [];

    /// <summary>
    /// Files ensured inside managed account directories (e.g. <c>config.toml</c> with <c>cli_auth_credentials_store = "file"</c> for Codex).
    /// </summary>
    public Dictionary<string, string>? InitFiles { get; set; }

    /// <summary>Non-secret local metadata file from which Octopus can read display identity (email / handle / tier).</summary>
    public AccountIdentityFileSpec? IdentityFile { get; set; }

    /// <summary>For <c>globalKeyring</c>: the shared OS keyring slot the CLI reads and writes.</summary>
    public KeyringSlotSpec? Keyring { get; set; }
}

public static class AccountModes
{
    public const string EnvDir = "envDir";
    public const string GlobalKeyring = "globalKeyring";
}

/// <summary>Describes where an <c>envDir</c> CLI records non-secret signed-in user metadata.</summary>
public sealed class AccountIdentityFileSpec
{
    /// <summary>Relative path inside the account directory (e.g. <c>.claude.json</c>, <c>auth.json</c>, <c>config.json</c>).</summary>
    public string Path { get; set; } = "";

    /// <summary>Optional path relative to <c>$HOME</c> used when reading the <c>default</c> account (e.g. <c>~/.claude.json</c>).</summary>
    public string? FallbackHomePath { get; set; }

    /// <summary>Parser kind: <c>jsonPaths</c> (default) or <c>codexAuthJwt</c>.</summary>
    public string Kind { get; set; } = "jsonPaths";

    /// <summary>Dot-separated JSON property paths tried in order for the identity string (e.g. <c>oauthAccount.emailAddress</c>).</summary>
    public List<string> JsonPaths { get; set; } = [];

    /// <summary>Optional dot-separated JSON property paths tried in order for the subscription plan/tier.</summary>
    public List<string>? TierJsonPaths { get; set; }
}

/// <summary>Identifies a third-party CLI's OS keyring entry (such as <c>go-keyring</c> used by Antigravity CLI).</summary>
public sealed class KeyringSlotSpec
{
    public string Service { get; set; } = "";
    public string Account { get; set; } = "";
    /// <summary>Windows Credential Manager target name; defaults to <c>&lt;Service&gt;:&lt;Account&gt;</c>.</summary>
    public string? WindowsTarget { get; set; }
    /// <summary>Linux Secret Service user attribute name; <c>go-keyring</c> uses <c>username</c>.</summary>
    public string LinuxUserAttribute { get; set; } = "username";
}

/// <summary>Where an account entry originated.</summary>
public enum AccountOrigin
{
    /// <summary>The CLI's implicit default directory (<c>~/.claude</c>, <c>~/.codex</c>, <c>~/.copilot</c>); id is <c>default</c>.</summary>
    Default,
    /// <summary>Created and managed by Octopus (isolated directory under <c>&lt;DataDirectory&gt;/accounts/&lt;agentId&gt;/&lt;id&gt;</c> or keyring snapshot).</summary>
    Managed,
    /// <summary>An existing directory on disk imported by the user; removing the entry never deletes its files.</summary>
    External,
}

/// <summary>Non-secret metadata for one configured agent account in <c>accounts.json</c>.</summary>
public sealed class AgentAccountRecord
{
    public const string DefaultId = "default";

    public string Id { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Label { get; set; } = "";
    public AccountOrigin Origin { get; set; } = AccountOrigin.Managed;
    /// <summary>Absolute directory path for <c>envDir</c> accounts; null for <c>globalKeyring</c> snapshots.</summary>
    public string? Directory { get; set; }
    /// <summary>Non-secret signed-in identifier (email, GitHub login, or organization name), if known.</summary>
    public string? Identity { get; set; }
    /// <summary>Non-secret subscription tier (e.g. <c>max</c>, <c>pro</c>, <c>plus</c>, <c>team</c>), if known.</summary>
    public string? PlanTier { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }

    [JsonIgnore]
    public bool IsDefaultOrigin => Origin == AccountOrigin.Default || string.Equals(Id, DefaultId, StringComparison.Ordinal);
}

/// <summary>The content of <c>&lt;DataDirectory&gt;/accounts.json</c>. Contains zero tokens or secrets.</summary>
public sealed class AccountsDocument
{
    public int Version { get; set; } = 1;
    public List<AgentAccountRecord> Accounts { get; set; } = [];
    /// <summary>Agent id to the currently active snapshot account id for <c>globalKeyring</c> agents.</summary>
    public Dictionary<string, string> ActiveGlobalAccounts { get; set; } = [];
}
