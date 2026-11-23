using Octopus.Core.Agents;
using Octopus.Core.Projects;

namespace Octopus.Agents.Accounts;

/// <summary>Result of resolving which subscription account a launch will use (design/13-多账户切换.md §4.3).</summary>
public sealed record ResolvedAgentAccount(
    AgentAccountRecord? Account,
    IReadOnlyDictionary<string, string>? Environment,
    IReadOnlyList<string>? UnsetEnvironment);

/// <summary>Prepares and finishes a new keyring-based account sign-in (e.g. Antigravity CLI).</summary>
public sealed record KeyringLoginPreparation(
    string? PreviousCredential,
    string? PreviousActiveAccountId);

/// <summary>
/// Manages subscription accounts across <c>envDir</c> agents (Claude, Codex, Copilot) and <c>globalKeyring</c> agents
/// (Antigravity CLI). Metadata lives in <c>accounts.json</c>; keyring snapshots live exclusively in <c>ISecretStore</c>.
/// </summary>
public interface IAgentAccountService
{
    event EventHandler? Changed;

    /// <summary>
    /// All accounts configured for <paramref name="definition"/>. For <c>envDir</c> agents, always includes the implicit
    /// <c>default</c> account first.
    /// </summary>
    IReadOnlyList<AgentAccountRecord> GetAccounts(AgentDefinition definition);

    /// <summary>For <c>globalKeyring</c> agents, returns the currently active snapshot account id (if known).</summary>
    string? GetActiveGlobalAccountId(string agentId);

    /// <summary>
    /// Resolves the effective account and launch environment using 4-tier precedence:
    /// <c>Launch.AccountId</c> (or legacy <c>Launch.Env</c>) → <c>Project.AgentAccounts[id]</c> →
    /// <c>Profile.DefaultAccountId</c> → <c>default</c>.
    /// </summary>
    ResolvedAgentAccount Resolve(
        AgentDefinition definition,
        AgentProfile profile,
        Project? project = null,
        AgentLaunchOptions? launch = null);

    /// <summary>
    /// Resolves which account id is pre-selected in UI for <paramref name="definition"/> given <paramref name="profile"/>
    /// and <paramref name="project"/> (Project → Profile → ActiveGlobal / Default).
    /// </summary>
    string? ResolvePreferredAccountId(AgentDefinition definition, AgentProfile profile, Project? project = null);

    /// <summary>
    /// Ensures a managed account's directory exists (0700 on Unix) and its <c>initFiles</c> (such as Codex's
    /// <c>cli_auth_credentials_store = "file"</c>) are present before running a session or login command.
    /// </summary>
    void EnsurePrepared(AgentDefinition definition, AgentAccountRecord account);

    /// <summary>Builds the <see cref="AgentCommand"/> to run inside the embedded login terminal for <paramref name="account"/>.</summary>
    AgentCommand BuildLoginCommand(
        AgentDefinition definition,
        AgentProfile profile,
        string? installedPath,
        AgentAccountRecord? account = null);

    /// <summary>Creates a new Octopus-managed isolated directory account (<c>&lt;DataDirectory&gt;/accounts/&lt;agentId&gt;/&lt;id&gt;</c>).</summary>
    AgentAccountRecord CreateManaged(AgentDefinition definition, string label);

    /// <summary>Imports an existing configuration directory as an <see cref="AccountOrigin.External"/> account.</summary>
    AgentAccountRecord ImportDirectory(AgentDefinition definition, string directory, string? label = null);

    /// <summary>Renames an account's display label.</summary>
    void Rename(string agentId, string accountId, string newLabel);

    /// <summary>
    /// Removes an account. Deletes its directory only when <paramref name="deleteManagedFiles"/> is true and
    /// <see cref="AgentAccountRecord.Origin"/> is <see cref="AccountOrigin.Managed"/>. Deletes keyring snapshots from <c>ISecretStore</c>.
    /// </summary>
    Task RemoveAsync(AgentDefinition definition, string accountId, bool deleteManagedFiles = true, CancellationToken cancellationToken = default);

    /// <summary>Reads non-secret identity metadata (email / GitHub login / plan tier) from local config files and updates <c>accounts.json</c>.</summary>
    Task<AgentAccountRecord?> RefreshIdentityAsync(AgentDefinition definition, string accountId, CancellationToken cancellationToken = default);

    /// <summary>Refreshes identity metadata for all accounts of <paramref name="definition"/>.</summary>
    Task RefreshAllIdentitiesAsync(AgentDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Checks whether the live OS keyring slot for a <c>globalKeyring</c> agent currently holds a credential.</summary>
    Task<bool> HasLiveKeyringCredentialAsync(AgentDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Captures the live OS keyring credential into a new (or existing <paramref name="overwriteAccountId"/>) snapshot in <c>ISecretStore</c>.</summary>
    Task<AgentAccountRecord> CaptureLiveKeyringAsync(
        AgentDefinition definition,
        string label,
        string? overwriteAccountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the live OS keyring slot so <c>agy</c> prompts for a fresh OAuth login, returning previous state to restore if cancelled.</summary>
    Task<KeyringLoginPreparation> PrepareNewKeyringLoginAsync(AgentDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Completes a new keyring login: saves the newly written credential as <paramref name="label"/>, or restores <paramref name="preparation"/> if none was written.</summary>
    Task<AgentAccountRecord?> CompleteNewKeyringLoginAsync(
        AgentDefinition definition,
        string label,
        KeyringLoginPreparation preparation,
        CancellationToken cancellationToken = default);

    /// <summary>Writes the saved snapshot of <paramref name="accountId"/> from <c>ISecretStore</c> into the live OS keyring slot.</summary>
    Task SwitchGlobalKeyringAsync(AgentDefinition definition, string accountId, CancellationToken cancellationToken = default);
}
