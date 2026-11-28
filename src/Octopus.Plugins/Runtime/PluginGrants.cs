using Octopus.Core.Plugins;

namespace Octopus.Plugins.Runtime;

/// <summary>
/// The consent decision shown to the user for a staged install or upgrade and committed verbatim if confirmed.
/// </summary>
public sealed record PluginInstallConsent(
    PluginManifest Manifest,
    bool IsVerifiedUpgrade,
    IReadOnlyList<PermissionRequest> Required,
    IReadOnlyList<PermissionRequest> PreservedOptional,
    IReadOnlyList<PermissionRequest> UngrantedOptional,
    IReadOnlyList<PluginPermissionGrant> PreservedDenied);

/// <summary>
/// What one plugin may do right now: a permission counts only when the manifest declares it, the user granted a
/// compatible scope and access mode (built-in plugins come with their declared permissions granted, design/plugins/12 §6),
/// and it was not denied.
/// </summary>
public sealed class PluginGrants
{
    private readonly bool _builtin;
    private readonly bool _isIdentifiedInstalledRecord;
    private readonly HashSet<string> _declared;
    private readonly Dictionary<string, string> _legacyGrants;
    private readonly Dictionary<string, List<PluginPermissionGrant>> _scopedByKey;
    private readonly Dictionary<string, List<PluginPermissionGrant>> _scopedDeniedByKey;
    private readonly HashSet<string> _denied;

    public PluginGrants(PluginManifest manifest, PluginRecord? record, bool builtin)
        : this(manifest, record, builtin, packageSource: null)
    {
    }

    public PluginGrants(PluginManifest manifest, PluginRecord? record, bool builtin, PluginSource? packageSource)
    {
        Manifest = manifest;
        Record = record;
        _builtin = builtin;
        _isIdentifiedInstalledRecord = IsIdentifiedInstalledRecord(record, manifest, packageSource);
        _declared = new HashSet<string>(manifest.Permissions.Requests.Select(r => r.Key), StringComparer.Ordinal);
        _denied = new HashSet<string>(record?.Denied ?? [], StringComparer.Ordinal);
        _legacyGrants = new Dictionary<string, string>(StringComparer.Ordinal);
        _scopedByKey = new Dictionary<string, List<PluginPermissionGrant>>(StringComparer.Ordinal);
        _scopedDeniedByKey = new Dictionary<string, List<PluginPermissionGrant>>(StringComparer.Ordinal);

        foreach (var (key, value) in record?.Grants ?? [])
        {
            _legacyGrants[key] = value;
        }
        foreach (var grant in record?.ScopedGrants ?? [])
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.Key)) continue;
            if (!_scopedByKey.TryGetValue(grant.Key, out var list))
            {
                list = [];
                _scopedByKey[grant.Key] = list;
            }
            list.Add(grant);
        }
        foreach (var denied in record?.ScopedDenied ?? [])
        {
            if (denied is null || string.IsNullOrWhiteSpace(denied.Key)) continue;
            if (!_scopedDeniedByKey.TryGetValue(denied.Key, out var list))
            {
                list = [];
                _scopedDeniedByKey[denied.Key] = list;
            }
            list.Add(denied);
        }
    }

    public PluginManifest Manifest { get; }
    public PluginRecord? Record { get; }

    public bool IsDeclared(string key) => _declared.Contains(key);

    public bool Has(string key)
    {
        if (!_declared.Contains(key)) return false;
        if (_builtin) return true;

        var matching = Manifest.Permissions.Requests.Where(r => r.Key == key).ToList();
        if (matching.Any(IsGranted)) return true;
        if (IsListScopedKey(key) && Details(key).Count > 0) return true;
        return false;
    }

    /// <summary>True when this specific declared permission request (including its scope and mode) was explicitly declined.</summary>
    public bool IsDenied(PermissionRequest request)
    {
        if (_builtin) return false;
        if (_scopedDeniedByKey.TryGetValue(request.Key, out var scopedDenied))
        {
            var target = FromRequest(request);
            return scopedDenied.Any(d => SameGrant(d, target));
        }
        return _denied.Contains(request.Key);
    }

    /// <summary>True when this specific declared permission request (including its scope and mode) is currently granted.</summary>
    public bool IsGranted(PermissionRequest request)
    {
        if (!_declared.Contains(request.Key) || IsDenied(request)) return false;
        if (_builtin) return true;

        if (_scopedByKey.TryGetValue(request.Key, out var scoped))
        {
            return scoped.Any(g => Covers(g, request));
        }

        return _legacyGrants.TryGetValue(request.Key, out var legacyValue)
            && LegacyCovers(request, legacyValue, Manifest, _isIdentifiedInstalledRecord);
    }

    /// <summary>True when <paramref name="request"/> is effective only because another active declared request covers it.</summary>
    public bool IsImpliedByAnotherGrant(PermissionRequest request)
    {
        if (_builtin || !_declared.Contains(request.Key) || IsDenied(request)) return false;
        if (!_scopedByKey.TryGetValue(request.Key, out var scoped)) return false;
        var target = FromRequest(request);
        if (scoped.Any(g => SameGrant(g, target))) return false;
        return Manifest.Permissions.Requests.Any(other =>
            other.Key == request.Key
            && !SameGrant(FromRequest(other), target)
            && IsGranted(other)
            && Covers(FromRequest(other), request));
    }

    /// <summary>The granted level (<c>read</c>, <c>readwrite</c>, <c>granted</c>…); null when not held.</summary>
    public string? Level(string key)
    {
        if (!Has(key)) return null;
        if (_builtin)
        {
            return Manifest.Permissions.Requests.FirstOrDefault(r => r.Key == key)?.Details.FirstOrDefault() ?? "granted";
        }
        if (_scopedByKey.TryGetValue(key, out var scoped))
        {
            if (scoped.Any(g => string.Equals(g.Access, "readwrite", StringComparison.OrdinalIgnoreCase))) return "readwrite";
            return scoped.FirstOrDefault()?.Access ?? "granted";
        }
        return _legacyGrants.TryGetValue(key, out var legacyValue) ? legacyValue : null;
    }

    /// <summary>Throws the error the plugin sees when it lacks <paramref name="key"/>.</summary>
    public void Require(string key)
    {
        if (!Has(key))
        {
            throw PluginApiException.Denied(key, canRequest: _declared.Contains(key) && !IsKeyFullyDenied(key));
        }
    }

    private bool IsKeyFullyDenied(string key)
    {
        var matching = Manifest.Permissions.Requests.Where(r => r.Key == key).ToList();
        if (matching.Count > 0) return matching.All(IsDenied);
        return _denied.Contains(key);
    }

    /// <summary>The details of a declared list permission (net hosts, runnable programs) restricted to accepted entries.</summary>
    public IReadOnlyList<string> Details(string key)
    {
        if (!_declared.Contains(key)) return [];
        var request = Manifest.Permissions.Requests.FirstOrDefault(r => r.Key == key);
        if (request is null || IsDenied(request)) return [];
        if (_builtin) return request.Details;

        if (IsListScopedKey(key))
        {
            if (_scopedByKey.TryGetValue(key, out var scoped))
            {
                var allowed = new HashSet<string>(scoped.SelectMany(g => g.Details ?? []), StringComparer.Ordinal);
                return request.Details.Where(allowed.Contains).ToList();
            }
            if (_legacyGrants.TryGetValue(key, out var legacyValue)
                && LegacyCovers(request, legacyValue, Manifest, _isIdentifiedInstalledRecord))
            {
                return request.Details;
            }
            return [];
        }

        return Has(key) ? request.Details : [];
    }

    public bool TryGetProjectScope(FsProjectRequest project, out FileAccessKind access)
    {
        var request = new PermissionRequest(
            "fs.project",
            project.Access == FileAccessKind.ReadWrite ? PermissionRisk.High : PermissionRisk.Medium,
            project.Optional,
            [AccessText(project.Access), .. project.Include],
            Exclude: project.Exclude);
        if (IsDenied(request))
        {
            access = default;
            return false;
        }
        if (_builtin)
        {
            access = project.Access;
            return true;
        }

        if (_scopedByKey.TryGetValue("fs.project", out var scoped))
        {
            if (scoped.Any(g => Covers(g, request)))
            {
                access = project.Access;
                return true;
            }
            access = default;
            return false;
        }

        if (_legacyGrants.TryGetValue("fs.project", out var legacyValue))
        {
            access = string.Equals(legacyValue, "readwrite", StringComparison.OrdinalIgnoreCase)
                ? project.Access
                : FileAccessKind.Read;
            return true;
        }

        access = default;
        return false;
    }

    public bool IsExternalGranted(FsPathRequest external)
    {
        var request = new PermissionRequest(
            "fs.external",
            PermissionRisk.High,
            external.Optional,
            [external.Path, AccessText(external.Access)],
            external.Reason,
            external.Localized);
        return IsGranted(request);
    }

    public bool IsAgentDataGranted(FsAgentDataRequest agentData)
    {
        var request = new PermissionRequest(
            $"fs.agentData.{agentData.Agent}",
            agentData.Access == FileAccessKind.ReadWrite ? PermissionRisk.High : PermissionRisk.Medium,
            agentData.Optional,
            [AccessText(agentData.Access), .. agentData.Include],
            agentData.Reason,
            agentData.Localized);
        return IsGranted(request);
    }

    // ---------------------------------------------------------------- scope-bound grant reconciliation

    public static PluginPermissionGrant FromRequest(PermissionRequest request)
    {
        var key = request.Key;
        if (key == "fs.project")
        {
            var access = request.Details.FirstOrDefault() ?? "read";
            var include = NormalizeSet(request.Details.Skip(1));
            var exclude = NormalizeSet(request.Exclude);
            return new PluginPermissionGrant(key, access, include, exclude);
        }
        if (key == "fs.external")
        {
            var path = request.Details.FirstOrDefault();
            var access = request.Details.ElementAtOrDefault(1) ?? "read";
            return new PluginPermissionGrant(key, access, path is null ? [] : [path]);
        }
        if (key.StartsWith("fs.agentData.", StringComparison.Ordinal))
        {
            var access = request.Details.FirstOrDefault() ?? "read";
            var include = NormalizeSet(request.Details.Skip(1));
            return new PluginPermissionGrant(key, access, include);
        }
        if (IsListScopedKey(key))
        {
            return new PluginPermissionGrant(key, "granted", NormalizeSet(request.Details));
        }
        var simpleAccess = key.StartsWith("agents.signals.", StringComparison.Ordinal)
            || key.StartsWith("agents.probes.", StringComparison.Ordinal)
            ? "granted"
            : request.Details.FirstOrDefault() ?? "granted";
        return new PluginPermissionGrant(key, simpleAccess);
    }

    /// <summary>True when an accepted <paramref name="grant"/> covers <paramref name="request"/> without broadening scope or mode.</summary>
    public static bool Covers(PluginPermissionGrant grant, PermissionRequest request)
    {
        if (!string.Equals(grant.Key, request.Key, StringComparison.Ordinal)) return false;

        var key = request.Key;
        if (key == "fs.project")
        {
            var reqAccess = request.Details.FirstOrDefault() ?? "read";
            if (!AccessCovers(grant.Access, reqAccess)) return false;
            var reqInclude = request.Details.Skip(1).ToList();
            if (grant.Details is { Count: > 0 })
            {
                if (reqInclude.Count == 0 || !reqInclude.All(p => grant.Details.Contains(p, StringComparer.Ordinal)))
                {
                    return false;
                }
            }
            var reqExclude = request.Exclude ?? [];
            if (grant.Exclude is { Count: > 0 } && !grant.Exclude.All(e => reqExclude.Contains(e, StringComparer.Ordinal)))
            {
                return false;
            }
            return true;
        }

        if (key == "fs.external")
        {
            var reqPath = request.Details.FirstOrDefault();
            var reqAccess = request.Details.ElementAtOrDefault(1) ?? "read";
            var grantedPath = grant.Details?.FirstOrDefault();
            return !string.IsNullOrEmpty(reqPath)
                && string.Equals(grantedPath, reqPath, StringComparison.Ordinal)
                && AccessCovers(grant.Access, reqAccess);
        }

        if (key.StartsWith("fs.agentData.", StringComparison.Ordinal))
        {
            var reqAccess = request.Details.FirstOrDefault() ?? "read";
            var reqInclude = request.Details.Skip(1).ToList();
            return AccessCovers(grant.Access, reqAccess)
                && grant.Details is { Count: > 0 }
                && reqInclude.Count > 0
                && reqInclude.All(p => grant.Details.Contains(p, StringComparer.Ordinal));
        }

        if (IsListScopedKey(key))
        {
            return grant.Details is { Count: > 0 }
                && request.Details.Count > 0
                && request.Details.All(item => grant.Details.Contains(item, StringComparer.Ordinal));
        }

        var simpleAccess = key.StartsWith("agents.signals.", StringComparison.Ordinal)
            || key.StartsWith("agents.probes.", StringComparison.Ordinal)
            ? "granted"
            : request.Details.FirstOrDefault() ?? "granted";
        return AccessCovers(grant.Access, simpleAccess);
    }

    /// <summary>True when <paramref name="record"/> provably matches the installed <see cref="PluginSource.File"/> manifest.</summary>
    public static bool IsIdentifiedInstalledRecord(PluginRecord? record, PluginManifest? manifest, PluginSource? packageSource = null)
    {
        if (record is null || manifest is null) return false;
        if (packageSource is { } source && source != PluginSource.File) return false;
        if (record.Source != PluginSource.File) return false;
        if (string.IsNullOrWhiteSpace(record.Version)) return false;
        return string.Equals(record.Version, manifest.Version, StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes the exact consent agreement for a staged install or upgrade so the confirmation dialog and commit path
    /// share one decision. Optional grants are preserved only for verified upgrades from an identified installed manifest.
    /// </summary>
    public static PluginInstallConsent PlanInstall(
        PluginManifest newManifest,
        PluginRecord? existingRecord,
        bool isUpgrade,
        PluginManifest? previousManifest = null)
    {
        var required = newManifest.Permissions.Requests.Where(r => !r.Optional).ToList();
        var isVerifiedUpgrade = isUpgrade
            && previousManifest is not null
            && string.Equals(previousManifest.Id, newManifest.Id, StringComparison.Ordinal)
            && IsIdentifiedInstalledRecord(existingRecord, previousManifest, PluginSource.File);

        if (!isVerifiedUpgrade)
        {
            var ungranted = newManifest.Permissions.Requests.Where(r => r.Optional).ToList();
            return new PluginInstallConsent(newManifest, false, required, [], ungranted, []);
        }

        var prior = MaterializePriorGrants(existingRecord!, previousManifest, PluginSource.File);
        var preservedOptional = newManifest.Permissions.Requests
            .Where(r => r.Optional && !IsRecordDenied(existingRecord!, r) && prior.Any(g => Covers(g, r)))
            .ToList();
        var preservedSet = new HashSet<PermissionRequest>(preservedOptional, ReferenceEqualityComparer.Instance);
        var ungrantedOptional = newManifest.Permissions.Requests
            .Where(r => r.Optional && !preservedSet.Contains(r))
            .ToList();
        var preservedDenied = ungrantedOptional
            .Where(r => IsRecordDenied(existingRecord!, r))
            .Select(FromRequest)
            .ToList();

        return new PluginInstallConsent(newManifest, true, required, preservedOptional, ungrantedOptional, preservedDenied);
    }

    /// <summary>Returns optional requests in <paramref name="newManifest"/> that stay covered by compatible prior grants.</summary>
    public static IReadOnlyList<PermissionRequest> GetPreservedOptionalRequests(
        PluginRecord? existingRecord,
        PluginManifest newManifest,
        PluginManifest? previousManifest = null) =>
        PlanInstall(newManifest, existingRecord, isUpgrade: previousManifest is not null, previousManifest).PreservedOptional;

    /// <summary>
    /// Commits a precomputed <see cref="PluginInstallConsent"/> verbatim into <paramref name="record"/>.
    /// </summary>
    public static void ApplyInstallGrants(PluginRecord record, PluginInstallConsent consent)
    {
        var next = new List<PluginPermissionGrant>();
        foreach (var request in consent.Required)
        {
            AddOrReplaceGrant(next, FromRequest(request));
        }
        foreach (var request in consent.PreservedOptional)
        {
            AddOrReplaceGrant(next, FromRequest(request));
        }

        record.ScopedGrants = next;
        record.ScopedDenied = [];
        record.Denied = [];

        if (consent.IsVerifiedUpgrade)
        {
            foreach (var denied in consent.PreservedDenied)
            {
                AddOrReplaceGrant(record.ScopedDenied, denied);
            }
            SyncLegacyDenied(record, consent.Manifest);
        }

        SyncLegacySummary(record);
    }

    /// <summary>
    /// Reconciles persisted grants after the user confirms an install or upgrade: grants all required requests of
    /// <paramref name="newManifest"/>, preserves compatible prior optional grants only for verified upgrades without
    /// broadening them, and drops stale or broadened optional grants until explicitly consented.
    /// </summary>
    public static void ApplyInstallGrants(
        PluginRecord record,
        PluginManifest newManifest,
        PluginManifest? previousManifest = null) =>
        ApplyInstallGrants(record, PlanInstall(newManifest, record, isUpgrade: previousManifest is not null, previousManifest));

    /// <summary>True when an old-format record has identified provenance and can be migrated to scope-bound grants at startup.</summary>
    public static bool NeedsLegacyMigration(PluginRecord? record, PluginManifest? manifest, PluginSource packageSource = PluginSource.File) =>
        record is not null
        && record.ScopedGrants.Count == 0
        && record.Grants.Count > 0
        && IsIdentifiedInstalledRecord(record, manifest, packageSource);

    /// <summary>Migrates an identified old-format record into <see cref="PluginRecord.ScopedGrants"/> without granting unverified paths.</summary>
    public static bool TryMigrateLegacyRecord(PluginRecord record, PluginManifest manifest, PluginSource packageSource = PluginSource.File)
    {
        if (!NeedsLegacyMigration(record, manifest, packageSource)) return false;
        var migrated = MaterializePriorGrants(record, manifest, packageSource);
        if (migrated.Count == 0) return false;
        record.ScopedGrants = migrated;
        SyncLegacySummary(record);
        return true;
    }

    /// <summary>Grants or revokes a single declared permission request while keeping sibling scopes and access modes independent.</summary>
    public static void SetGrant(PluginRecord record, PluginManifest? manifest, PermissionRequest request, bool granted) =>
        SetGrant(record, manifest, request, granted, packageSource: null);

    /// <summary>Grants or revokes a single declared permission request while keeping sibling scopes and access modes independent.</summary>
    public static void SetGrant(PluginRecord record, PluginManifest? manifest, PermissionRequest request, bool granted, PluginSource? packageSource)
    {
        EnsureMaterialized(record, manifest, packageSource);
        MaterializeLegacyDeniedForKey(record, manifest, request.Key);
        var target = FromRequest(request);

        if (granted)
        {
            record.ScopedDenied.RemoveAll(d => SameGrant(d, target));
            AddOrReplaceGrant(record.ScopedGrants, target);
        }
        else
        {
            record.ScopedGrants.RemoveAll(g =>
                SameGrant(g, target)
                || (Covers(g, request) && !SupportsOtherActiveDeclaredRequest(g, record, manifest, request.Key, target)));
            AddOrReplaceGrant(record.ScopedDenied, target);
            SyncLegacyDenied(record, manifest, request.Key);
        }

        SyncLegacySummary(record);
    }

    private static bool SupportsOtherActiveDeclaredRequest(
        PluginPermissionGrant grant,
        PluginRecord record,
        PluginManifest? manifest,
        string key,
        PluginPermissionGrant excludedTarget) =>
        manifest is not null && manifest.Permissions.Requests.Any(other =>
            other.Key == key
            && !SameGrant(FromRequest(other), excludedTarget)
            && !IsRecordDenied(record, other)
            && Covers(grant, other));

    private static void MaterializeLegacyDeniedForKey(PluginRecord record, PluginManifest? manifest, string key)
    {
        if (!record.Denied.Contains(key, StringComparer.Ordinal)) return;
        var hasScopedDeniedForKey = record.ScopedDenied.Any(d => d is not null && string.Equals(d.Key, key, StringComparison.Ordinal));
        if (!hasScopedDeniedForKey && manifest is not null)
        {
            foreach (var declared in manifest.Permissions.Requests.Where(r => r.Key == key))
            {
                AddOrReplaceGrant(record.ScopedDenied, FromRequest(declared));
            }
        }
        record.Denied.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
    }

    private static bool IsRecordDenied(PluginRecord record, PermissionRequest request)
    {
        var scopedForKey = record.ScopedDenied
            .Where(d => d is not null && string.Equals(d.Key, request.Key, StringComparison.Ordinal))
            .ToList();
        if (scopedForKey.Count > 0)
        {
            var target = FromRequest(request);
            return scopedForKey.Any(d => SameGrant(d, target));
        }
        return record.Denied.Contains(request.Key, StringComparer.Ordinal);
    }

    private static void EnsureMaterialized(PluginRecord record, PluginManifest? manifest, PluginSource? packageSource)
    {
        var prior = MaterializePriorGrants(record, manifest, packageSource);
        if (prior.Count > record.ScopedGrants.Count)
        {
            record.ScopedGrants = prior;
        }
    }

    private static List<PluginPermissionGrant> MaterializePriorGrants(
        PluginRecord record,
        PluginManifest? previousManifest,
        PluginSource? packageSource = null)
    {
        var list = new List<PluginPermissionGrant>(record.ScopedGrants.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Key)));
        var scopedKeys = new HashSet<string>(list.Select(g => g.Key), StringComparer.Ordinal);
        var denied = new HashSet<string>(record.Denied, StringComparer.Ordinal);
        var identified = IsIdentifiedInstalledRecord(record, previousManifest, packageSource);

        foreach (var (key, legacyValue) in record.Grants)
        {
            if (scopedKeys.Contains(key) || denied.Contains(key)) continue;

            if (previousManifest is not null)
            {
                foreach (var prevReq in previousManifest.Permissions.Requests.Where(r => r.Key == key))
                {
                    if (!LegacyCovers(prevReq, legacyValue, previousManifest, identified)) continue;
                    var materialized = FromRequest(prevReq);
                    if (key == "fs.project"
                        && !string.Equals(legacyValue, "readwrite", StringComparison.OrdinalIgnoreCase))
                    {
                        materialized = materialized with { Access = "read" };
                    }
                    else if (key.StartsWith("fs.agentData.", StringComparison.Ordinal)
                        && prevReq.Optional
                        && !string.Equals(legacyValue, "readwrite", StringComparison.OrdinalIgnoreCase))
                    {
                        materialized = materialized with { Access = "read" };
                    }
                    AddOrReplaceGrant(list, materialized);
                }
            }
            else if (key != "fs.external"
                && key != "fs.project"
                && !key.StartsWith("fs.agentData.", StringComparison.Ordinal)
                && !IsListScopedKey(key))
            {
                AddOrReplaceGrant(list, new PluginPermissionGrant(key, legacyValue));
            }
        }

        return list;
    }

    private static bool LegacyCovers(
        PermissionRequest request,
        string legacyValue,
        PluginManifest manifest,
        bool isIdentifiedInstalledRecord)
    {
        var key = request.Key;
        if (key == "fs.project")
        {
            var reqAccess = request.Details.FirstOrDefault() ?? "read";
            return AccessCovers(legacyValue, reqAccess);
        }
        if (key == "fs.external")
        {
            var reqPath = request.Details.FirstOrDefault();
            if (string.IsNullOrEmpty(reqPath)) return false;
            if (!isIdentifiedInstalledRecord) return false;

            var requiredExternal = manifest.Permissions.Requests
                .Where(r => r.Key == "fs.external" && !r.Optional)
                .ToList();
            if (requiredExternal.Count > 0)
            {
                var lastRequiredPath = requiredExternal[^1].Details.FirstOrDefault();
                if (string.Equals(legacyValue, lastRequiredPath, StringComparison.Ordinal)
                    || string.Equals(legacyValue, "granted", StringComparison.OrdinalIgnoreCase))
                {
                    return !request.Optional;
                }
            }

            if (!string.Equals(legacyValue, reqPath, StringComparison.Ordinal))
            {
                return false;
            }
            if (request.Optional && requiredExternal.Any(r => string.Equals(r.Details.FirstOrDefault(), reqPath, StringComparison.Ordinal)))
            {
                return false;
            }
            var reqAccess = request.Details.ElementAtOrDefault(1) ?? "read";
            if (string.Equals(reqAccess, "read", StringComparison.OrdinalIgnoreCase)) return true;
            return !manifest.Permissions.Requests.Any(r =>
                r.Key == "fs.external"
                && string.Equals(r.Details.FirstOrDefault(), reqPath, StringComparison.Ordinal)
                && string.Equals(r.Details.ElementAtOrDefault(1) ?? "read", "read", StringComparison.OrdinalIgnoreCase));
        }
        if (key.StartsWith("fs.agentData.", StringComparison.Ordinal))
        {
            var reqAccess = request.Details.FirstOrDefault() ?? "read";
            var requiredForKey = manifest.Permissions.Requests.Where(r => r.Key == key && !r.Optional).ToList();
            if (isIdentifiedInstalledRecord && requiredForKey.Count > 0)
            {
                var lastRequiredAccess = requiredForKey[^1].Details.FirstOrDefault() ?? "read";
                if (!request.Optional
                    && (string.Equals(legacyValue, lastRequiredAccess, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(legacyValue, "granted", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            if (!AccessCovers(legacyValue, reqAccess)) return false;
            var sameKeyCount = manifest.Permissions.Requests.Count(r => r.Key == key);
            return !request.Optional || sameKeyCount == 1;
        }
        if (IsListScopedKey(key))
        {
            return string.Equals(legacyValue, "granted", StringComparison.OrdinalIgnoreCase)
                || request.Details.Contains(legacyValue, StringComparer.Ordinal);
        }
        return true;
    }

    private static void SyncLegacyDenied(PluginRecord record, PluginManifest? manifest, string? onlyKey = null)
    {
        IEnumerable<string> keys = onlyKey is not null
            ? [onlyKey]
            : record.ScopedDenied.Select(d => d.Key)
                .Concat(record.Denied)
                .Concat(manifest?.Permissions.Requests.Select(r => r.Key) ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToList();

        foreach (var key in keys)
        {
            var hasActiveGrant = record.ScopedGrants.Any(g => string.Equals(g.Key, key, StringComparison.Ordinal));
            if (hasActiveGrant)
            {
                record.Denied.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
                continue;
            }

            bool allDenied;
            if (manifest is not null)
            {
                var declared = manifest.Permissions.Requests.Where(r => r.Key == key).ToList();
                allDenied = declared.Count > 0
                    && declared.All(r => record.ScopedDenied.Any(d => SameGrant(d, FromRequest(r))));
            }
            else
            {
                allDenied = record.ScopedDenied.Any(d => string.Equals(d.Key, key, StringComparison.Ordinal));
            }

            if (allDenied)
            {
                if (!record.Denied.Contains(key, StringComparer.Ordinal))
                {
                    record.Denied.Add(key);
                }
            }
            else
            {
                record.Denied.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
            }
        }
    }

    private static void SyncLegacySummary(PluginRecord record)
    {
        record.Grants.Clear();
        foreach (var grant in record.ScopedGrants)
        {
            if (string.IsNullOrWhiteSpace(grant.Key)) continue;
            var summary = grant.Key == "fs.external" || IsListScopedKey(grant.Key)
                ? grant.Details?.FirstOrDefault() ?? grant.Access ?? "granted"
                : grant.Access ?? "granted";
            record.Grants.TryAdd(grant.Key, summary);
        }
    }

    private static void AddOrReplaceGrant(List<PluginPermissionGrant> list, PluginPermissionGrant grant)
    {
        list.RemoveAll(existing => SameGrant(existing, grant));
        list.Add(grant);
    }

    private static bool SameGrant(PluginPermissionGrant a, PluginPermissionGrant b) =>
        string.Equals(a.Key, b.Key, StringComparison.Ordinal)
        && string.Equals(a.Access, b.Access, StringComparison.OrdinalIgnoreCase)
        && SetEqualOrdinal(a.Details, b.Details)
        && SetEqualOrdinal(a.Exclude, b.Exclude);

    private static List<string> NormalizeSet(IEnumerable<string>? items) =>
        items is null ? [] : items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static bool SetEqualOrdinal(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        var aEmpty = a is null || a.Count == 0;
        var bEmpty = b is null || b.Count == 0;
        if (aEmpty || bEmpty) return aEmpty && bEmpty;
        return new HashSet<string>(a!, StringComparer.Ordinal).SetEquals(b!);
    }

    private static bool IsListScopedKey(string key) =>
        key is "net" or "shell.open" or "process.run" or "process.run.devices";

    private static bool AccessCovers(string? grantedAccess, string requestedAccess)
    {
        if (string.Equals(requestedAccess, "readwrite", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(grantedAccess, "readwrite", StringComparison.OrdinalIgnoreCase);
        }
        if (string.Equals(requestedAccess, "read", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(grantedAccess, "read", StringComparison.OrdinalIgnoreCase)
                || string.Equals(grantedAccess, "readwrite", StringComparison.OrdinalIgnoreCase)
                || string.Equals(grantedAccess, "granted", StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(grantedAccess, requestedAccess, StringComparison.OrdinalIgnoreCase)
            || string.Equals(grantedAccess, "granted", StringComparison.OrdinalIgnoreCase);
    }

    private static string AccessText(FileAccessKind access) =>
        access == FileAccessKind.ReadWrite ? "readwrite" : "read";
}
