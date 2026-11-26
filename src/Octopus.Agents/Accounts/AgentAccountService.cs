using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Octopus.Agents.Signals;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.Agents.Accounts;

public sealed class AgentAccountService : IAgentAccountService
{
    public const string KeyringSnapshotServicePrefix = "Octopus/accounts/";

    private readonly IAccountStore _store;
    private readonly IAppPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly ISignalHub? _signals;
    private readonly IShellCommandBuilder _shell;
    private readonly string _home;

    public AgentAccountService(
        IAccountStore store,
        IAppPaths paths,
        ISecretStore secrets,
        IShellCommandBuilder shell,
        ISignalHub? signals = null,
        string? home = null)
    {
        _store = store;
        _paths = paths;
        _secrets = secrets;
        _shell = shell;
        _signals = signals;
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AgentAccountRecord> GetAccounts(AgentDefinition definition)
    {
        if (definition.Accounts is not { } cap) return [];

        var saved = _store.Current.Accounts
            .Where(a => string.Equals(a.AgentId, definition.Id, StringComparison.Ordinal))
            .ToList();

        if (!string.Equals(cap.Mode, AccountModes.EnvDir, StringComparison.OrdinalIgnoreCase))
        {
            return saved
                .OrderBy(a => a.CreatedAt)
                .ToList();
        }

        var defaultDir = ResolveDefaultDirectory(definition);
        var defaultRecord = saved.FirstOrDefault(a => a.IsDefaultOrigin);
        if (defaultRecord is null)
        {
            var identity = ReadLocalIdentity(definition, defaultDir, isDefault: true);
            defaultRecord = new AgentAccountRecord
            {
                Id = AgentAccountRecord.DefaultId,
                AgentId = definition.Id,
                Label = "Default",
                Origin = AccountOrigin.Default,
                Directory = defaultDir,
                Identity = identity.Identity,
                PlanTier = identity.PlanTier,
                CreatedAt = DateTimeOffset.MinValue,
            };
        }
        else
        {
            defaultRecord = CloneRecord(defaultRecord);
            defaultRecord.Directory ??= defaultDir;
            if (string.IsNullOrEmpty(defaultRecord.Identity))
            {
                var identity = ReadLocalIdentity(definition, defaultRecord.Directory, isDefault: true);
                defaultRecord.Identity = identity.Identity;
                defaultRecord.PlanTier ??= identity.PlanTier;
            }
        }

        var others = saved
            .Where(a => !a.IsDefaultOrigin)
            .Select(a =>
            {
                var copy = CloneRecord(a);
                if (string.IsNullOrEmpty(copy.Identity) && !string.IsNullOrEmpty(copy.Directory))
                {
                    var identity = ReadLocalIdentity(definition, copy.Directory, isDefault: false);
                    copy.Identity = identity.Identity;
                    copy.PlanTier ??= identity.PlanTier;
                }
                return copy;
            })
            .OrderBy(a => a.CreatedAt)
            .ToList();

        return [defaultRecord, .. others];
    }

    public string? GetActiveGlobalAccountId(string agentId)
    {
        if (!_store.Current.ActiveGlobalAccounts.TryGetValue(agentId, out var id) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        return _store.Current.Accounts.Any(a => a.AgentId == agentId && a.Id == id) ? id : null;
    }

    public string? ResolvePreferredAccountId(AgentDefinition definition, AgentProfile profile, Project? project = null)
    {
        if (definition.Accounts is not { } cap) return null;
        var accounts = GetAccounts(definition);
        if (accounts.Count == 0) return null;

        bool Exists(string? id) => !string.IsNullOrWhiteSpace(id) && accounts.Any(a => a.Id == id);

        if (project is not null && project.AgentAccounts.TryGetValue(definition.Id, out var projectAcc) && Exists(projectAcc))
        {
            return projectAcc;
        }
        if (Exists(profile.DefaultAccountId))
        {
            return profile.DefaultAccountId;
        }
        if (string.Equals(cap.Mode, AccountModes.GlobalKeyring, StringComparison.OrdinalIgnoreCase))
        {
            var active = GetActiveGlobalAccountId(definition.Id);
            if (Exists(active)) return active;
            return accounts[0].Id;
        }
        return AgentAccountRecord.DefaultId;
    }

    public ResolvedAgentAccount Resolve(
        AgentDefinition definition,
        AgentProfile profile,
        Project? project = null,
        AgentLaunchOptions? launch = null)
    {
        if (definition.Accounts is not { } cap)
        {
            return new ResolvedAgentAccount(null, null, null);
        }

        var unsets = cap.UnsetEnv is { Count: > 0 } list ? (IReadOnlyList<string>)list : null;

        if (string.Equals(cap.Mode, AccountModes.GlobalKeyring, StringComparison.OrdinalIgnoreCase))
        {
            var accounts = GetAccounts(definition);
            var chosenId = FirstValidId(accounts, launch?.AccountId, project?.AgentAccounts.GetValueOrDefault(definition.Id), profile.DefaultAccountId, GetActiveGlobalAccountId(definition.Id));
            var chosen = chosenId is null ? null : accounts.FirstOrDefault(a => a.Id == chosenId);
            return new ResolvedAgentAccount(chosen, null, accounts.Count > 0 ? unsets : null);
        }

        if (string.IsNullOrEmpty(cap.Env))
        {
            return new ResolvedAgentAccount(null, null, unsets);
        }

        // Legacy session migration sets Launch.Env[envVar] directly; honor it if AccountId is not explicitly set.
        if (string.IsNullOrWhiteSpace(launch?.AccountId)
            && launch?.Env is not null
            && launch.Env.TryGetValue(cap.Env, out var explicitDir)
            && !string.IsNullOrWhiteSpace(explicitDir))
        {
            var normalizedExplicit = SignalAccounts.NormalizeDirectory(explicitDir, _home);
            var matching = GetAccounts(definition).FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.Directory)
                && PathEquals(SignalAccounts.NormalizeDirectory(a.Directory, _home), normalizedExplicit));
            if (matching is not null)
            {
                EnsurePrepared(definition, matching);
            }
            return new ResolvedAgentAccount(
                matching,
                new Dictionary<string, string>(StringComparer.Ordinal) { [cap.Env] = normalizedExplicit },
                unsets);
        }

        var all = GetAccounts(definition);
        var effectiveId = FirstValidId(
            all,
            launch?.AccountId,
            project?.AgentAccounts.GetValueOrDefault(definition.Id),
            profile.DefaultAccountId,
            AgentAccountRecord.DefaultId) ?? AgentAccountRecord.DefaultId;

        var account = all.FirstOrDefault(a => a.Id == effectiveId) ?? all[0];
        EnsurePrepared(definition, account);

        if (!string.IsNullOrWhiteSpace(account.Directory) && Directory.Exists(account.Directory))
        {
            _signals?.RegisterAccount(definition.Id, account.Directory, account.IsDefaultOrigin ? null : account.Label);
        }

        // When no additional accounts are configured (only the implicit default exists), leave the user's shell
        // API keys and config directory variables intact so third-party API gateways work out of the box.
        if (all.Count <= 1 && account.IsDefaultOrigin)
        {
            return new ResolvedAgentAccount(account, null, null);
        }

        Dictionary<string, string>? env = null;
        if (account.IsDefaultOrigin)
        {
            // For the implicit default account (e.g. ~/.claude), leave CLAUDE_CONFIG_DIR unset so Claude reads ~/.claude.json,
            // UNLESS we need to unset a stray CLAUDE_CONFIG_DIR from the user's shell!
            var extraUnsets = new List<string>();
            if (unsets is not null) extraUnsets.AddRange(unsets);
            if (!extraUnsets.Contains(cap.Env, StringComparer.Ordinal))
            {
                extraUnsets.Add(cap.Env);
            }
            unsets = extraUnsets;
        }
        else if (!string.IsNullOrWhiteSpace(account.Directory))
        {
            env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [cap.Env] = account.Directory,
            };
        }

        return new ResolvedAgentAccount(account, env, unsets);
    }

    public void EnsurePrepared(AgentDefinition definition, AgentAccountRecord account)
    {
        if (definition.Accounts is not { Mode: AccountModes.EnvDir } cap) return;
        if (account.Origin != AccountOrigin.Managed || string.IsNullOrWhiteSpace(account.Directory)) return;

        CreatePrivateDirectory(account.Directory);
        if (cap.InitFiles is null) return;

        foreach (var (relativePath, content) in cap.InitFiles)
        {
            var fullPath = Path.Combine(account.Directory, relativePath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (!File.Exists(fullPath))
            {
                File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            }
            else if (string.Equals(relativePath, "config.toml", StringComparison.OrdinalIgnoreCase)
                     && content.Contains("cli_auth_credentials_store", StringComparison.Ordinal))
            {
                // Ensure Codex's file credential store stays configured even if Codex wrote other keys to config.toml.
                try
                {
                    var existing = File.ReadAllText(fullPath);
                    if (!existing.Contains("cli_auth_credentials_store", StringComparison.Ordinal))
                    {
                        var prefix = content.EndsWith('\n') ? content : content + "\n";
                        File.WriteAllText(fullPath, prefix + existing, new UTF8Encoding(false));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public AgentCommand BuildLoginCommand(
        AgentDefinition definition,
        AgentProfile profile,
        string? installedPath,
        AgentAccountRecord? account = null)
    {
        string executable;
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.Command))
        {
            var parts = _shell.Split(profile.Command);
            if (parts.Count > 0)
            {
                executable = parts[0];
                args.AddRange(parts.Skip(1));
            }
            else
            {
                executable = DefaultExecutable(definition, installedPath);
            }
        }
        else
        {
            executable = DefaultExecutable(definition, installedPath);
        }

        var cap = definition.Accounts;
        if (cap?.LoginArgs is { Count: > 0 })
        {
            args.AddRange(cap.LoginArgs);
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in definition.Capabilities.BaseEnv) env[k] = v;

        var unsets = new List<string>();
        if (cap?.UnsetEnv is { Count: > 0 })
        {
            unsets.AddRange(cap.UnsetEnv);
        }

        if (cap is { Mode: AccountModes.EnvDir, Env.Length: > 0 } && account is not null)
        {
            EnsurePrepared(definition, account);
            if (account.IsDefaultOrigin)
            {
                if (!unsets.Contains(cap.Env, StringComparer.Ordinal)) unsets.Add(cap.Env);
            }
            else if (!string.IsNullOrWhiteSpace(account.Directory))
            {
                env[cap.Env] = account.Directory;
            }
        }

        foreach (var (k, v) in profile.Env)
        {
            // Do not let a profile API key override interactive OAuth login!
            if (unsets.Contains(k, StringComparer.Ordinal)) continue;
            if (cap?.Env is { } accountVar && k == accountVar && account is not null) continue;
            env[k] = v;
        }

        return new AgentCommand(executable, args, env, unsets.Count > 0 ? unsets : null);
    }

    public AgentAccountRecord CreateManaged(AgentDefinition definition, string label)
    {
        if (definition.Accounts is not { Mode: AccountModes.EnvDir })
        {
            throw new InvalidOperationException($"Agent '{definition.Id}' does not use directory-isolated accounts.");
        }

        var id = NewAccountId(definition.Id);
        var directory = Path.Combine(_paths.DataDirectory, "accounts", definition.Id, id);
        var record = new AgentAccountRecord
        {
            Id = id,
            AgentId = definition.Id,
            Label = string.IsNullOrWhiteSpace(label) ? id : label.Trim(),
            Origin = AccountOrigin.Managed,
            Directory = directory,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        EnsurePrepared(definition, record);
        _store.Update(doc => doc.Accounts.Add(CloneRecord(record)));
        _signals?.RegisterAccount(definition.Id, directory, record.Label);
        return record;
    }

    public AgentAccountRecord ImportDirectory(AgentDefinition definition, string directory, string? label = null)
    {
        if (definition.Accounts is not { Mode: AccountModes.EnvDir })
        {
            throw new InvalidOperationException($"Agent '{definition.Id}' does not use directory-isolated accounts.");
        }
        var normalized = SignalAccounts.NormalizeDirectory(directory, _home);
        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"Account directory '{directory}' does not exist.");
        }

        var existing = GetAccounts(definition).FirstOrDefault(a =>
            !string.IsNullOrEmpty(a.Directory) && PathEquals(SignalAccounts.NormalizeDirectory(a.Directory, _home), normalized));
        if (existing is not null)
        {
            return existing;
        }

        var identity = ReadLocalIdentity(definition, normalized, isDefault: false);
        var id = NewAccountId(definition.Id);
        var chosenLabel = !string.IsNullOrWhiteSpace(label)
            ? label.Trim()
            : identity.Identity ?? SignalAccounts.DefaultLabel(normalized);

        var record = new AgentAccountRecord
        {
            Id = id,
            AgentId = definition.Id,
            Label = chosenLabel,
            Origin = AccountOrigin.External,
            Directory = normalized,
            Identity = identity.Identity,
            PlanTier = identity.PlanTier,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _store.Update(doc => doc.Accounts.Add(CloneRecord(record)));
        _signals?.RegisterAccount(definition.Id, normalized, record.Label);
        return record;
    }

    public void Rename(string agentId, string accountId, string newLabel)
    {
        var trimmed = newLabel.Trim();
        if (trimmed.Length == 0) return;

        string? directory = null;
        _store.Update(doc =>
        {
            var existing = doc.Accounts.FirstOrDefault(a => a.AgentId == agentId && a.Id == accountId);
            if (existing is not null)
            {
                existing.Label = trimmed;
                directory = existing.Directory;
            }
            else if (accountId == AgentAccountRecord.DefaultId)
            {
                doc.Accounts.Add(new AgentAccountRecord
                {
                    Id = AgentAccountRecord.DefaultId,
                    AgentId = agentId,
                    Label = trimmed,
                    Origin = AccountOrigin.Default,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        });
        if (!string.IsNullOrEmpty(directory))
        {
            _signals?.RegisterAccount(agentId, directory, trimmed);
        }
    }

    public async Task RemoveAsync(AgentDefinition definition, string accountId, bool deleteManagedFiles = true, CancellationToken cancellationToken = default)
    {
        if (accountId == AgentAccountRecord.DefaultId)
        {
            return;
        }

        var target = _store.Current.Accounts.FirstOrDefault(a => a.AgentId == definition.Id && a.Id == accountId);
        if (target is null) return;

        _store.Update(doc =>
        {
            doc.Accounts.RemoveAll(a => a.AgentId == definition.Id && a.Id == accountId);
            if (doc.ActiveGlobalAccounts.TryGetValue(definition.Id, out var active) && active == accountId)
            {
                doc.ActiveGlobalAccounts.Remove(definition.Id);
            }
        });

        if (definition.Accounts is { Mode: AccountModes.GlobalKeyring } && _secrets.IsAvailable)
        {
            try
            {
                await _secrets.DeleteAsync(SnapshotService(definition.Id), accountId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or NotSupportedException)
            {
            }
        }

        if (deleteManagedFiles
            && target.Origin == AccountOrigin.Managed
            && !string.IsNullOrWhiteSpace(target.Directory)
            && IsInsideManagedRoot(definition.Id, target.Directory))
        {
            try
            {
                if (Directory.Exists(target.Directory))
                {
                    Directory.Delete(target.Directory, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public Task<AgentAccountRecord?> RefreshIdentityAsync(AgentDefinition definition, string accountId, CancellationToken cancellationToken = default)
    {
        var accounts = GetAccounts(definition);
        var target = accounts.FirstOrDefault(a => a.Id == accountId);
        if (target is null || string.IsNullOrWhiteSpace(target.Directory))
        {
            return Task.FromResult(target);
        }

        var (identity, tier) = ReadLocalIdentity(definition, target.Directory, target.IsDefaultOrigin);
        if (identity is null && tier is null)
        {
            return Task.FromResult<AgentAccountRecord?>(target);
        }

        _store.Update(doc =>
        {
            var saved = doc.Accounts.FirstOrDefault(a => a.AgentId == definition.Id && a.Id == accountId);
            if (saved is not null)
            {
                if (identity is not null) saved.Identity = identity;
                if (tier is not null) saved.PlanTier = tier;
            }
            else if (target.IsDefaultOrigin)
            {
                doc.Accounts.Add(new AgentAccountRecord
                {
                    Id = AgentAccountRecord.DefaultId,
                    AgentId = definition.Id,
                    Label = target.Label,
                    Origin = AccountOrigin.Default,
                    Directory = target.Directory,
                    Identity = identity,
                    PlanTier = tier,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        });

        return Task.FromResult(GetAccounts(definition).FirstOrDefault(a => a.Id == accountId));
    }

    public async Task RefreshAllIdentitiesAsync(AgentDefinition definition, CancellationToken cancellationToken = default)
    {
        foreach (var account in GetAccounts(definition))
        {
            await RefreshIdentityAsync(definition, account.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- globalKeyring (Antigravity CLI) ----

    public async Task<bool> HasLiveKeyringCredentialAsync(AgentDefinition definition, CancellationToken cancellationToken = default)
    {
        if (!_secrets.IsAvailable || SlotOf(definition) is not { } slot) return false;
        var value = await _secrets.GetExternalAsync(slot, cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(value);
    }

    public async Task<AgentAccountRecord> CaptureLiveKeyringAsync(
        AgentDefinition definition,
        string label,
        string? overwriteAccountId = null,
        CancellationToken cancellationToken = default)
    {
        var slot = SlotOf(definition) ?? throw new InvalidOperationException($"Agent '{definition.Id}' has no keyring slot.");
        var live = await _secrets.GetExternalAsync(slot, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(live))
        {
            throw new InvalidOperationException("No active login credential was found in the system keychain.");
        }

        var id = !string.IsNullOrWhiteSpace(overwriteAccountId) ? overwriteAccountId! : NewAccountId(definition.Id);
        await _secrets.SetAsync(SnapshotService(definition.Id), id, live, cancellationToken).ConfigureAwait(false);

        AgentAccountRecord? result = null;
        _store.Update(doc =>
        {
            var existing = doc.Accounts.FirstOrDefault(a => a.AgentId == definition.Id && a.Id == id);
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(label)) existing.Label = label.Trim();
                existing.LastUsedAt = DateTimeOffset.UtcNow;
                result = CloneRecord(existing);
            }
            else
            {
                var created = new AgentAccountRecord
                {
                    Id = id,
                    AgentId = definition.Id,
                    Label = string.IsNullOrWhiteSpace(label) ? id : label.Trim(),
                    Origin = AccountOrigin.Managed,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastUsedAt = DateTimeOffset.UtcNow,
                };
                doc.Accounts.Add(created);
                result = CloneRecord(created);
            }
            doc.ActiveGlobalAccounts[definition.Id] = id;
        });

        return result!;
    }

    public async Task<KeyringLoginPreparation> PrepareNewKeyringLoginAsync(AgentDefinition definition, CancellationToken cancellationToken = default)
    {
        var slot = SlotOf(definition) ?? throw new InvalidOperationException($"Agent '{definition.Id}' has no keyring slot.");
        var current = await _secrets.GetExternalAsync(slot, cancellationToken).ConfigureAwait(false);
        var activeId = GetActiveGlobalAccountId(definition.Id);
        if (!string.IsNullOrEmpty(current))
        {
            await _secrets.DeleteExternalAsync(slot, cancellationToken).ConfigureAwait(false);
        }
        return new KeyringLoginPreparation(current, activeId);
    }

    public async Task<AgentAccountRecord?> CompleteNewKeyringLoginAsync(
        AgentDefinition definition,
        string label,
        KeyringLoginPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        var slot = SlotOf(definition) ?? throw new InvalidOperationException($"Agent '{definition.Id}' has no keyring slot.");
        var fresh = await _secrets.GetExternalAsync(slot, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(fresh))
        {
            return await CaptureLiveKeyringAsync(definition, label, null, cancellationToken).ConfigureAwait(false);
        }

        // User cancelled or exited before signing in: restore the previous live credential so nothing is lost.
        if (!string.IsNullOrEmpty(preparation.PreviousCredential))
        {
            await _secrets.SetExternalAsync(slot, preparation.PreviousCredential, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(preparation.PreviousActiveAccountId))
            {
                _store.Update(doc => doc.ActiveGlobalAccounts[definition.Id] = preparation.PreviousActiveAccountId!);
            }
        }
        return null;
    }

    public async Task SwitchGlobalKeyringAsync(AgentDefinition definition, string accountId, CancellationToken cancellationToken = default)
    {
        var slot = SlotOf(definition) ?? throw new InvalidOperationException($"Agent '{definition.Id}' has no keyring slot.");
        var snapshot = await _secrets.GetAsync(SnapshotService(definition.Id), accountId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(snapshot))
        {
            throw new InvalidOperationException($"Saved credential snapshot '{accountId}' was not found in the system keychain.");
        }

        await _secrets.SetExternalAsync(slot, snapshot, cancellationToken).ConfigureAwait(false);
        _store.Update(doc =>
        {
            doc.ActiveGlobalAccounts[definition.Id] = accountId;
            if (doc.Accounts.FirstOrDefault(a => a.AgentId == definition.Id && a.Id == accountId) is { } record)
            {
                record.LastUsedAt = DateTimeOffset.UtcNow;
            }
        });
    }

    // ---- helpers ----

    private string ResolveDefaultDirectory(AgentDefinition definition)
    {
        var raw = definition.Accounts?.DefaultDir ?? definition.Signals?.Account?.Default;
        return string.IsNullOrWhiteSpace(raw) ? "" : SignalAccounts.NormalizeDirectory(raw, _home);
    }

    private static string SnapshotService(string agentId) => KeyringSnapshotServicePrefix + agentId;

    private static ExternalKeyringSlot? SlotOf(AgentDefinition definition) =>
        definition.Accounts is { Mode: AccountModes.GlobalKeyring, Keyring: { Service.Length: > 0, Account.Length: > 0 } k }
            ? new ExternalKeyringSlot(k.Service, k.Account, k.WindowsTarget, k.LinuxUserAttribute)
            : null;

    private string NewAccountId(string agentId)
    {
        var existing = _store.Current.Accounts
            .Where(a => a.AgentId == agentId)
            .Select(a => a.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var candidate = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            if (candidate != AgentAccountRecord.DefaultId && !existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private bool IsInsideManagedRoot(string agentId, string directory)
    {
        var root = Path.GetFullPath(Path.Combine(_paths.DataDirectory, "accounts", agentId));
        var full = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(directory, privateMode);
        try
        {
            File.SetUnixFileMode(directory, privateMode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? FirstValidId(IReadOnlyList<AgentAccountRecord> accounts, params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && accounts.Any(a => a.Id == candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(
            a.TrimEnd('/', '\\'),
            b.TrimEnd('/', '\\'),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string DefaultExecutable(AgentDefinition def, string? installedPath)
    {
        if (!string.IsNullOrWhiteSpace(installedPath))
        {
            var i = installedPath.LastIndexOfAny(['/', '\\']);
            var name = i >= 0 ? installedPath[(i + 1)..] : installedPath;
            if (name.Length > 0) return name;
        }
        return def.Detect.Executables.Count > 0 ? def.Detect.Executables[0] : def.Id;
    }

    private static AgentAccountRecord CloneRecord(AgentAccountRecord r) => new()
    {
        Id = r.Id,
        AgentId = r.AgentId,
        Label = r.Label,
        Origin = r.Origin,
        Directory = r.Directory,
        Identity = r.Identity,
        PlanTier = r.PlanTier,
        CreatedAt = r.CreatedAt,
        LastUsedAt = r.LastUsedAt,
    };

    internal (string? Identity, string? PlanTier) ReadLocalIdentity(AgentDefinition definition, string directory, bool isDefault)
    {
        if (definition.Accounts?.IdentityFile is not { Path.Length: > 0 } spec || string.IsNullOrWhiteSpace(directory))
        {
            return (null, null);
        }

        var filePath = Path.Combine(directory, spec.Path);
        if (!File.Exists(filePath) && isDefault && !string.IsNullOrWhiteSpace(spec.FallbackHomePath))
        {
            filePath = Path.Combine(_home, spec.FallbackHomePath);
        }
        if (!File.Exists(filePath)) return (null, null);

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            if (string.Equals(spec.Kind, "codexAuthJwt", StringComparison.OrdinalIgnoreCase))
            {
                return ReadCodexJwtIdentity(root);
            }

            string? identity = null;
            foreach (var path in spec.JsonPaths)
            {
                if (TryGetJsonString(root, path) is { Length: > 0 } found)
                {
                    identity = found;
                    break;
                }
            }

            string? tier = null;
            if (spec.TierJsonPaths is { Count: > 0 } tierPaths)
            {
                foreach (var path in tierPaths)
                {
                    if (TryGetJsonString(root, path) is { Length: > 0 } found)
                    {
                        tier = found;
                        break;
                    }
                }
            }
            return (identity, tier);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Extracts non-secret email and plan tier from Codex's <c>auth.json</c> <c>id_token</c> JWT payload.</summary>
    private static (string? Identity, string? PlanTier) ReadCodexJwtIdentity(JsonElement root)
    {
        var token = TryGetJsonString(root, "tokens.id_token") ?? TryGetJsonString(root, "id_token");
        if (string.IsNullOrEmpty(token)) return (null, null);

        var parts = token.Split('.');
        if (parts.Length < 2) return (null, null);

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var bytes = Convert.FromBase64String(payload);
            using var jwtDoc = JsonDocument.Parse(bytes);
            var claims = jwtDoc.RootElement;
            if (claims.ValueKind != JsonValueKind.Object) return (null, null);

            var email = TryGetJsonString(claims, "email")
                        ?? (claims.TryGetProperty("https://api.openai.com/profile", out var profile)
                            && profile.ValueKind == JsonValueKind.Object
                            && profile.TryGetProperty("email", out var pe)
                            && pe.ValueKind == JsonValueKind.String ? pe.GetString() : null);

            string? tier = null;
            if (claims.TryGetProperty("https://api.openai.com/auth", out var auth)
                && auth.ValueKind == JsonValueKind.Object
                && auth.TryGetProperty("chatgpt_plan_type", out var pt)
                && pt.ValueKind == JsonValueKind.String)
            {
                tier = pt.GetString();
            }

            return (string.IsNullOrWhiteSpace(email) ? null : email, string.IsNullOrWhiteSpace(tier) ? null : tier);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return (null, null);
        }
    }

    private static string? TryGetJsonString(JsonElement element, string dottedPath)
    {
        var current = element;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
