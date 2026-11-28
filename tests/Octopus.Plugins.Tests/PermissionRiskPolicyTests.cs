using Octopus.Core.Plugins;
using Octopus.Plugins.Manifests;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Octopus.Storage;
using Xunit;

namespace Octopus.Plugins.Tests;

public sealed class PermissionRiskPolicyTests
{
    private static ManifestReadResult Parse(string permissionsJson) => PluginManifestReader.Parse($$"""
        {
          "schema": 1,
          "id": "acme.sample",
          "name": "Sample",
          "version": "1.0.0",
          "engines": { "api": 0 },
          "permissions": {{permissionsJson}}
        }
        """, null, PluginSource.File);

    [Fact]
    public void Secrets_permission_is_classified_as_high_risk()
    {
        var required = Parse("""{ "secrets": true }""");
        Assert.True(required.IsValid, string.Join("\n", required.Issues));
        var req = Assert.Single(required.Manifest!.Permissions.Requests, r => r.Key == "secrets");
        Assert.Equal(PermissionRisk.High, req.Risk);
        Assert.False(req.Optional);

        var optional = Parse("""{ "secrets": { "optional": true } }""");
        Assert.True(optional.IsValid, string.Join("\n", optional.Issues));
        var optReq = Assert.Single(optional.Manifest!.Permissions.Requests, r => r.Key == "secrets");
        Assert.Equal(PermissionRisk.High, optReq.Risk);
        Assert.True(optReq.Optional);
    }

    [Fact]
    public void Fs_project_distinguishes_read_medium_and_readwrite_high_risk()
    {
        var readOnly = Parse("""{ "fs": { "project": { "access": "read" } } }""");
        Assert.True(readOnly.IsValid, string.Join("\n", readOnly.Issues));
        var readReq = Assert.Single(readOnly.Manifest!.Permissions.Requests, r => r.Key == "fs.project");
        Assert.Equal(PermissionRisk.Medium, readReq.Risk);
        Assert.Equal(["read"], readReq.Details);

        var readWrite = Parse("""{ "fs": { "project": { "access": "readwrite" } } }""");
        Assert.True(readWrite.IsValid, string.Join("\n", readWrite.Issues));
        var writeReq = Assert.Single(readWrite.Manifest!.Permissions.Requests, r => r.Key == "fs.project");
        Assert.Equal(PermissionRisk.High, writeReq.Risk);
        Assert.Equal(["readwrite"], writeReq.Details);
    }

    [Fact]
    public void Fs_external_is_classified_as_high_risk_while_preserving_read_and_write_access()
    {
        var result = Parse("""
            {
              "fs": {
                "external": [
                  { "path": "~/docs/**", "access": "read", "reason": "Read notes" },
                  { "path": "~/notes/**", "access": "readwrite", "reason": "Save notes" }
                ]
              }
            }
            """);
        Assert.True(result.IsValid, string.Join("\n", result.Issues));

        var external = result.Manifest!.Permissions.Requests.Where(r => r.Key == "fs.external").ToList();
        Assert.Equal(2, external.Count);
        Assert.Equal(PermissionRisk.High, external[0].Risk);
        Assert.Equal(["~/docs/**", "read"], external[0].Details);
        Assert.Equal(PermissionRisk.High, external[1].Risk);
        Assert.Equal(["~/notes/**", "readwrite"], external[1].Details);
    }

    [Fact]
    public void Fs_agentData_distinguishes_read_medium_and_readwrite_high_risk()
    {
        var result = Parse("""
            {
              "fs": {
                "agentData": [
                  { "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "read" },
                  { "agent": "codex", "include": ["sessions/**/*.jsonl"], "access": "readwrite" }
                ]
              }
            }
            """);
        Assert.True(result.IsValid, string.Join("\n", result.Issues));

        var requests = result.Manifest!.Permissions.Requests.ToDictionary(r => r.Key);
        Assert.Equal(PermissionRisk.Medium, requests["fs.agentData.claude"].Risk);
        Assert.Equal(PermissionRisk.High, requests["fs.agentData.codex"].Risk);
    }

    [Fact]
    public void Net_hosts_distinguishes_explicit_domains_medium_and_wildcard_high_risk()
    {
        var explicitHosts = Parse("""{ "net": { "hosts": ["api.example.test", "cdn.example.test"] } }""");
        Assert.True(explicitHosts.IsValid, string.Join("\n", explicitHosts.Issues));
        Assert.Equal(PermissionRisk.Medium, Assert.Single(explicitHosts.Manifest!.Permissions.Requests, r => r.Key == "net").Risk);

        var wildcardOnly = Parse("""{ "net": { "hosts": ["*.example.test"] } }""");
        Assert.True(wildcardOnly.IsValid, string.Join("\n", wildcardOnly.Issues));
        Assert.Equal(PermissionRisk.High, Assert.Single(wildcardOnly.Manifest!.Permissions.Requests, r => r.Key == "net").Risk);

        var mixedWildcard = Parse("""{ "net": { "hosts": ["api.example.test", "*.example.test"] } }""");
        Assert.True(mixedWildcard.IsValid, string.Join("\n", mixedWildcard.Issues));
        Assert.Equal(PermissionRisk.High, Assert.Single(mixedWildcard.Manifest!.Permissions.Requests, r => r.Key == "net").Risk);
    }

    [Theory]
    [InlineData("low", "git", PermissionRisk.Medium)]
    [InlineData("low", "git:read", PermissionRisk.Medium)]
    [InlineData(null, "git", PermissionRisk.Medium)]
    [InlineData("low", "clipboard.read", PermissionRisk.High)]
    [InlineData(null, "clipboard.read", PermissionRisk.High)]
    [InlineData("low", "fs.agentData.claude", PermissionRisk.High)]
    [InlineData("low", "fs.agentData", PermissionRisk.High)]
    [InlineData(null, "fs.agentData:read", PermissionRisk.High)]
    public void PermissionRiskCatalog_enforces_documented_marketplace_minimum_grades(
        string? serverRisk,
        string capability,
        PermissionRisk expected)
    {
        Assert.Equal(expected, PermissionRiskCatalog.EffectiveRisk(serverRisk, [capability]));
    }

    [Theory]
    [InlineData("*.example.test", PermissionRisk.High, true)]
    [InlineData("api.*.example.test", PermissionRisk.Medium, false)]
    [InlineData("foo*bar.example.test", PermissionRisk.Medium, false)]
    [InlineData("*", PermissionRisk.Medium, false)]
    public void Net_wildcard_risk_matches_runtime_leading_subdomain_wildcards_only(
        string hostPattern,
        PermissionRisk expectedRisk,
        bool matchesSubdomain)
    {
        var parsed = Parse($$"""{ "net": { "hosts": ["{{hostPattern}}"] } }""");
        Assert.True(parsed.IsValid, string.Join("\n", parsed.Issues));
        Assert.Equal(expectedRisk, Assert.Single(parsed.Manifest!.Permissions.Requests, r => r.Key == "net").Risk);
        Assert.Equal(expectedRisk, PermissionRiskCatalog.RiskOfDeclaredCapability($"net:{hostPattern}"));
        Assert.Equal(matchesSubdomain, NetModule.HostAllowed("sub.example.test", [hostPattern]));
        Assert.False(NetModule.HostAllowed("example.test", [hostPattern]));
    }

    [Fact]
    public void PluginGrants_reconciliation_preserves_compatible_legacy_optional_grants_without_silently_broadening()
    {
        var v1 = Parse("""
            {
              "net": { "hosts": ["api.example.test"], "optional": true },
              "process": { "run": ["git"], "optional": true },
              "fs": {
                "project": { "access": "read", "include": ["src/**"], "exclude": ["**/secrets/**"], "optional": true },
                "external": [
                  { "path": "~/docs/**", "access": "read", "reason": "Docs", "optional": true }
                ],
                "agentData": [
                  { "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "read", "optional": true }
                ]
              }
            }
            """).Manifest!;

        // Simulate a legacy plugins.json record (Grants only, empty ScopedGrants) where optional permissions were granted on v1.
        var legacyRecord = new PluginRecord
        {
            Enabled = true,
            Version = "1.0.0",
            Source = PluginSource.File,
            Grants =
            {
                ["net"] = "api.example.test",
                ["process.run"] = "git",
                ["fs.project"] = "read",
                ["fs.external"] = "~/docs/**",
                ["fs.agentData.claude"] = "read",
            },
        };

        // Compatible upgrade: same or narrower scopes stay preserved.
        var v2Compatible = Parse("""
            {
              "net": { "hosts": ["api.example.test"], "optional": true },
              "process": { "run": ["git"], "optional": true },
              "fs": {
                "project": { "access": "read", "include": ["src/**"], "exclude": ["**/secrets/**", "**/dist/**"], "optional": true },
                "external": [
                  { "path": "~/docs/**", "access": "read", "reason": "Docs", "optional": true }
                ],
                "agentData": [
                  { "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "read", "optional": true }
                ]
              }
            }
            """).Manifest!;

        var preserved = PluginGrants.GetPreservedOptionalRequests(legacyRecord, v2Compatible, v1);
        Assert.Equal(5, preserved.Count);

        var compatRecord = new PluginRecord
        {
            Enabled = true,
            Version = "1.0.0",
            Source = PluginSource.File,
            Grants = new Dictionary<string, string>(legacyRecord.Grants),
        };
        PluginGrants.ApplyInstallGrants(compatRecord, v2Compatible, v1);
        var compatGrants = new PluginGrants(v2Compatible, compatRecord, builtin: false);
        Assert.Equal(["api.example.test"], compatGrants.Details("net"));
        Assert.Equal(["git"], compatGrants.Details("process.run"));
        Assert.True(compatGrants.TryGetProjectScope(v2Compatible.Permissions.FsProject!, out var compatProjAccess));
        Assert.Equal(FileAccessKind.Read, compatProjAccess);
        Assert.True(compatGrants.IsExternalGranted(v2Compatible.Permissions.FsExternal[0]));
        Assert.True(compatGrants.IsAgentDataGranted(v2Compatible.Permissions.FsAgentData[0]));

        // Broadening upgrade: every broadened optional scope must be dropped rather than silently applied.
        var v2Broadened = Parse("""
            {
              "net": { "hosts": ["api.example.test", "*.evil.test"], "optional": true },
              "process": { "run": ["git", "sh"], "optional": true },
              "fs": {
                "project": { "access": "read", "include": ["src/**"], "optional": true },
                "external": [
                  { "path": "~/docs/**", "access": "readwrite", "reason": "Docs write", "optional": true }
                ],
                "agentData": [
                  { "agent": "claude", "include": ["projects/**/*.jsonl", "settings.json"], "access": "read", "optional": true }
                ]
              }
            }
            """).Manifest!;

        Assert.Empty(PluginGrants.GetPreservedOptionalRequests(legacyRecord, v2Broadened, v1));
        PluginGrants.ApplyInstallGrants(legacyRecord, v2Broadened, v1);
        var broadenedGrants = new PluginGrants(v2Broadened, legacyRecord, builtin: false);
        Assert.Empty(broadenedGrants.Details("net"));
        Assert.Empty(broadenedGrants.Details("process.run"));
        Assert.False(broadenedGrants.TryGetProjectScope(v2Broadened.Permissions.FsProject!, out _));
        Assert.False(broadenedGrants.IsExternalGranted(v2Broadened.Permissions.FsExternal[0]));
        Assert.False(broadenedGrants.IsAgentDataGranted(v2Broadened.Permissions.FsAgentData[0]));
    }

    [Fact]
    public void Fresh_install_with_surviving_or_dev_record_does_not_preserve_optional_grants()
    {
        var manifest = Parse("""
            {
              "net": { "hosts": ["api.example.test"], "optional": true },
              "fs": {
                "external": [
                  { "path": "~/docs/**", "access": "read", "reason": "Required docs" },
                  { "path": "~/kept/**", "access": "read", "reason": "Optional kept", "optional": true }
                ]
              }
            }
            """).Manifest!;

        // Surviving record from a deleted/stale File install (previousManifest is null => fresh install).
        var survivingFileRecord = new PluginRecord
        {
            Enabled = true,
            Version = "1.0.0",
            Source = PluginSource.File,
            Grants = { ["net"] = "api.example.test", ["fs.external"] = "~/kept/**" },
            ScopedGrants =
            {
                new PluginPermissionGrant("net", "granted", ["api.example.test"]),
                new PluginPermissionGrant("fs.external", "read", ["~/kept/**"]),
            },
        };

        Assert.Empty(PluginGrants.GetPreservedOptionalRequests(survivingFileRecord, manifest, previousManifest: null));
        PluginGrants.ApplyInstallGrants(survivingFileRecord, manifest, previousManifest: null);
        var freshGrants = new PluginGrants(manifest, survivingFileRecord, builtin: false);
        Assert.True(freshGrants.IsExternalGranted(manifest.Permissions.FsExternal[0]));
        Assert.False(freshGrants.IsExternalGranted(manifest.Permissions.FsExternal[1]));
        Assert.Empty(freshGrants.Details("net"));

        // Surviving record from a Dev copy, even if a package directory with v1.0.0 exists on disk.
        var devRecord = new PluginRecord
        {
            Enabled = true,
            Version = "1.0.0",
            Source = PluginSource.Dev,
            ScopedGrants =
            {
                new PluginPermissionGrant("net", "granted", ["api.example.test"]),
                new PluginPermissionGrant("fs.external", "read", ["~/kept/**"]),
            },
        };

        Assert.Empty(PluginGrants.GetPreservedOptionalRequests(devRecord, manifest, previousManifest: manifest));
        PluginGrants.ApplyInstallGrants(devRecord, manifest, previousManifest: manifest);
        var afterDev = new PluginGrants(manifest, devRecord, builtin: false);
        Assert.True(afterDev.IsExternalGranted(manifest.Permissions.FsExternal[0]));
        Assert.False(afterDev.IsExternalGranted(manifest.Permissions.FsExternal[1]));
        Assert.Empty(afterDev.Details("net"));

        // Mismatched version between state record and installed manifest on disk.
        var mismatchedVersionRecord = new PluginRecord
        {
            Enabled = true,
            Version = "0.9.0",
            Source = PluginSource.File,
            ScopedGrants =
            {
                new PluginPermissionGrant("net", "granted", ["api.example.test"]),
                new PluginPermissionGrant("fs.external", "read", ["~/kept/**"]),
            },
        };

        Assert.Empty(PluginGrants.GetPreservedOptionalRequests(mismatchedVersionRecord, manifest, previousManifest: manifest));
        PluginGrants.ApplyInstallGrants(mismatchedVersionRecord, manifest, previousManifest: manifest);
        var afterMismatch = new PluginGrants(manifest, mismatchedVersionRecord, builtin: false);
        Assert.True(afterMismatch.IsExternalGranted(manifest.Permissions.FsExternal[0]));
        Assert.False(afterMismatch.IsExternalGranted(manifest.Permissions.FsExternal[1]));
        Assert.Empty(afterMismatch.Details("net"));
    }

    [Fact]
    public void Declining_optional_write_preserves_required_read_on_same_target_and_keeps_siblings_independent()
    {
        var manifest = Parse("""
            {
              "fs": {
                "external": [
                  { "path": "~/docs/**", "access": "read", "reason": "Read docs" },
                  { "path": "~/docs/**", "access": "readwrite", "reason": "Write docs", "optional": true },
                  { "path": "~/other/**", "access": "read", "reason": "Other docs", "optional": true }
                ],
                "agentData": [
                  { "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "read" },
                  { "agent": "claude", "include": ["projects/**/*.jsonl"], "access": "readwrite", "optional": true }
                ]
              }
            }
            """).Manifest!;

        var extRead = manifest.Permissions.Requests.Single(r => r.Key == "fs.external" && r.Details[0] == "~/docs/**" && r.Details[1] == "read");
        var extWrite = manifest.Permissions.Requests.Single(r => r.Key == "fs.external" && r.Details[0] == "~/docs/**" && r.Details[1] == "readwrite");
        var extOther = manifest.Permissions.Requests.Single(r => r.Key == "fs.external" && r.Details[0] == "~/other/**");
        var agentRead = manifest.Permissions.Requests.Single(r => r.Key == "fs.agentData.claude" && r.Details[0] == "read");
        var agentWrite = manifest.Permissions.Requests.Single(r => r.Key == "fs.agentData.claude" && r.Details[0] == "readwrite");

        var record = new PluginRecord { Enabled = true, Version = "1.0.0", Source = PluginSource.File };
        PluginGrants.ApplyInstallGrants(record, manifest, previousManifest: null);

        // Declining optional readwrite on the same path MUST keep required read intact.
        PluginGrants.SetGrant(record, manifest, extWrite, granted: false);
        PluginGrants.SetGrant(record, manifest, agentWrite, granted: false);

        var afterDecline = new PluginGrants(manifest, record, builtin: false);
        Assert.True(afterDecline.IsGranted(extRead));
        Assert.False(afterDecline.IsGranted(extWrite));
        Assert.False(afterDecline.IsGranted(extOther));
        Assert.True(afterDecline.IsExternalGranted(manifest.Permissions.FsExternal[0]));
        Assert.False(afterDecline.IsExternalGranted(manifest.Permissions.FsExternal[1]));
        Assert.True(afterDecline.IsGranted(agentRead));
        Assert.False(afterDecline.IsGranted(agentWrite));
        Assert.True(afterDecline.IsAgentDataGranted(manifest.Permissions.FsAgentData[0]));
        Assert.False(afterDecline.IsAgentDataGranted(manifest.Permissions.FsAgentData[1]));

        // Granting optional readwrite and later revoking it MUST still leave required read intact.
        PluginGrants.SetGrant(record, manifest, extWrite, granted: true);
        var afterGrantWrite = new PluginGrants(manifest, record, builtin: false);
        Assert.True(afterGrantWrite.IsGranted(extRead));
        Assert.True(afterGrantWrite.IsGranted(extWrite));

        PluginGrants.SetGrant(record, manifest, extWrite, granted: false);
        var afterRevokeWrite = new PluginGrants(manifest, record, builtin: false);
        Assert.True(afterRevokeWrite.IsGranted(extRead));
        Assert.False(afterRevokeWrite.IsGranted(extWrite));
        Assert.True(afterRevokeWrite.IsDenied(extWrite));
        Assert.False(afterRevokeWrite.IsDenied(extRead));
        Assert.False(afterRevokeWrite.IsDenied(extOther));

        // Verified upgrade keeps the declined optional write denied while preserving required read,
        // and does not mark a newly added optional sibling on the same key as denied.
        var v2 = Parse("""
            {
              "fs": {
                "external": [
                  { "path": "~/docs/**", "access": "read", "reason": "Read docs" },
                  { "path": "~/docs/**", "access": "readwrite", "reason": "Write docs", "optional": true },
                  { "path": "~/new-opt/**", "access": "read", "reason": "New opt", "optional": true }
                ]
              }
            }
            """).Manifest!;
        var v2Read = v2.Permissions.Requests.Single(r => r.Details[0] == "~/docs/**" && r.Details[1] == "read");
        var v2Write = v2.Permissions.Requests.Single(r => r.Details[0] == "~/docs/**" && r.Details[1] == "readwrite");
        var v2NewOpt = v2.Permissions.Requests.Single(r => r.Details[0] == "~/new-opt/**");

        var consent = PluginGrants.PlanInstall(v2, record, isUpgrade: true, previousManifest: manifest);
        Assert.Empty(consent.PreservedOptional);
        // Even if the record is mutated after planning, committing the planned consent applies only what was agreed.
        record.ScopedGrants.Add(PluginGrants.FromRequest(extWrite));
        PluginGrants.ApplyInstallGrants(record, consent);

        var afterUpgrade = new PluginGrants(v2, record, builtin: false);
        Assert.True(afterUpgrade.IsGranted(v2Read));
        Assert.False(afterUpgrade.IsGranted(v2Write));
        Assert.True(afterUpgrade.IsDenied(v2Write));
        Assert.False(afterUpgrade.IsDenied(v2NewOpt));
    }

    [Fact]
    public void Old_format_record_recovers_all_required_external_paths_for_identified_installed_manifest_only()
    {
        var manifest = Parse("""
            {
              "fs": {
                "external": [
                  { "path": "~/docs/**", "access": "read", "reason": "Read docs" },
                  { "path": "~/notes/**", "access": "readwrite", "reason": "Save notes" },
                  { "path": "~/notes/**", "access": "readwrite", "reason": "Optional dup", "optional": true },
                  { "path": "~/extra/**", "access": "read", "reason": "Optional extra", "optional": true }
                ]
              }
            }
            """).Manifest!;

        // Actual base-format record written by old installer: only the last required external path was saved under "fs.external".
        var identifiedLegacy = new PluginRecord
        {
            Enabled = true,
            Version = "1.0.0",
            Source = PluginSource.File,
            Grants = { ["fs.external"] = "~/notes/**" },
        };

        var identifiedGrants = new PluginGrants(manifest, identifiedLegacy, builtin: false);
        Assert.True(identifiedGrants.IsExternalGranted(manifest.Permissions.FsExternal[0]));
        Assert.True(identifiedGrants.IsExternalGranted(manifest.Permissions.FsExternal[1]));
        Assert.False(identifiedGrants.IsExternalGranted(manifest.Permissions.FsExternal[3]));

        // Same path with required read + optional readwrite: old-format record must not silently grant optional write.
        var samePathManifest = Parse("""
            {
              "fs": {
                "external": [
                  { "path": "~/first/**", "access": "read", "reason": "First" },
                  { "path": "~/shared/**", "access": "read", "reason": "Shared read" },
                  { "path": "~/shared/**", "access": "readwrite", "reason": "Shared write", "optional": true }
                ]
              }
            }
            """).Manifest!;
        var samePathLegacy = new PluginRecord
        {
            Enabled = true,
            Version = "1.0.0",
            Source = PluginSource.File,
            Grants = { ["fs.external"] = "~/shared/**" },
        };
        var samePathGrants = new PluginGrants(samePathManifest, samePathLegacy, builtin: false);
        Assert.True(samePathGrants.IsExternalGranted(samePathManifest.Permissions.FsExternal[0]));
        Assert.True(samePathGrants.IsExternalGranted(samePathManifest.Permissions.FsExternal[1]));
        Assert.False(samePathGrants.IsExternalGranted(samePathManifest.Permissions.FsExternal[2]));

        // Uncertain provenance cases: version mismatch, missing version, Dev source, or non-matching legacy path.
        var versionMismatch = new PluginRecord
        {
            Enabled = true,
            Version = "0.9.0",
            Source = PluginSource.File,
            Grants = { ["fs.external"] = "~/notes/**" },
        };
        var mismatchGrants = new PluginGrants(manifest, versionMismatch, builtin: false);
        Assert.False(mismatchGrants.IsExternalGranted(manifest.Permissions.FsExternal[0]));
        Assert.False(mismatchGrants.IsExternalGranted(manifest.Permissions.FsExternal[1]));

        var missingVersion = new PluginRecord
        {
            Enabled = true,
            Version = null,
            Source = PluginSource.File,
            Grants = { ["fs.external"] = "~/notes/**" },
        };
        var missingVersionGrants = new PluginGrants(manifest, missingVersion, builtin: false);
        Assert.False(missingVersionGrants.IsExternalGranted(manifest.Permissions.FsExternal[0]));
        Assert.False(missingVersionGrants.IsExternalGranted(manifest.Permissions.FsExternal[1]));

        var devLegacy = new PluginRecord
        {
            Enabled = true,
            Version = "1.0.0",
            Source = PluginSource.Dev,
            Grants = { ["fs.external"] = "~/notes/**" },
        };
        var devGrants = new PluginGrants(manifest, devLegacy, builtin: false);
        Assert.False(devGrants.IsExternalGranted(manifest.Permissions.FsExternal[0]));
        Assert.False(devGrants.IsExternalGranted(manifest.Permissions.FsExternal[1]));

        var tamperedOrderLegacy = new PluginRecord
        {
            Enabled = true,
            Version = "1.0.0",
            Source = PluginSource.File,
            Grants = { ["fs.external"] = "~/docs/**" },
        };
        var tamperedGrants = new PluginGrants(manifest, tamperedOrderLegacy, builtin: false);
        Assert.False(tamperedGrants.IsExternalGranted(manifest.Permissions.FsExternal[1]));
    }

    [Fact]
    public void Old_key_only_denial_keeps_unselected_sibling_denied_when_one_is_enabled()
    {
        var manifest = Parse("""
            {
              "fs": {
                "external": [
                  { "path": "~/fixtures/a/**", "access": "read", "reason": "A", "optional": true },
                  { "path": "~/fixtures/b/**", "access": "read", "reason": "B", "optional": true }
                ]
              }
            }
            """).Manifest!;
        var a = manifest.Permissions.Requests[0];
        var b = manifest.Permissions.Requests[1];
        var record = new PluginRecord { Source = PluginSource.File, Version = manifest.Version, Denied = { "fs.external" } };

        PluginGrants.SetGrant(record, manifest, b, granted: true, PluginSource.File);
        var effective = new PluginGrants(manifest, record, builtin: false, PluginSource.File);

        Assert.True(effective.IsDenied(a));
        Assert.True(effective.IsGranted(b));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Reordering_set_valued_scope_preserves_denial_on_refresh_and_upgrade(bool upgrade, bool reorder)
    {
        const string original = """{ "net": { "hosts": ["a.example.test", "b.example.test"], "optional": true } }""";
        const string reordered = """{ "net": { "hosts": ["b.example.test", "a.example.test"], "optional": true } }""";
        var oldManifest = Parse(original).Manifest!;
        var nextJson = $$"""
            {
              "schema": 1,
              "id": "acme.sample",
              "name": "Sample",
              "version": "{{(upgrade ? "1.1.0" : "1.0.0")}}",
              "engines": { "api": 0 },
              "permissions": {{(reorder ? reordered : original)}}
            }
            """;
        var nextManifest = PluginManifestReader.Parse(nextJson, null, PluginSource.File).Manifest!;
        var record = new PluginRecord { Source = PluginSource.File, Version = oldManifest.Version };

        PluginGrants.SetGrant(record, oldManifest, Assert.Single(oldManifest.Permissions.Requests), granted: false, PluginSource.File);
        if (upgrade)
        {
            PluginGrants.ApplyInstallGrants(record, PluginGrants.PlanInstall(nextManifest, record, isUpgrade: true, oldManifest));
        }

        Assert.True(new PluginGrants(nextManifest, record, builtin: false, PluginSource.File).IsDenied(Assert.Single(nextManifest.Permissions.Requests)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Old_settings_selected_optional_external_path_survives_migration_and_upgrade(bool upgrade)
    {
        const string permissions = """
            {
              "theme": true,
              "fs": {
                "external": [
                  { "path": "~/fixtures/a/**", "access": "read", "reason": "A" },
                  { "path": "~/fixtures/b/**", "access": "read", "reason": "B", "optional": true }
                ]
              }
            }
            """;
        using var paths = new TempAppPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(Path.Combine(paths.DataDirectory, "plugins.json"), """
            {
              "version": 1,
              "plugins": {
                "acme.sample": {
                  "enabled": true,
                  "source": "file",
                  "version": "1.0.0",
                  "grants": {
                    "theme": "granted",
                    "fs.external": "~/fixtures/b/**"
                  },
                  "denied": []
                }
              }
            }
            """);
        var store = new JsonPluginStateStore(paths);
        store.Load();
        var oldManifest = Parse(permissions).Manifest!;
        var currentManifest = upgrade
            ? PluginManifestReader.Parse($$"""
                {
                  "schema": 1,
                  "id": "acme.sample",
                  "name": "Sample",
                  "version": "1.1.0",
                  "engines": { "api": 0 },
                  "permissions": {{permissions}}
                }
                """, null, PluginSource.File).Manifest!
            : oldManifest;

        var record = store.Current.Find("acme.sample")!;
        if (upgrade)
        {
            var consent = PluginGrants.PlanInstall(currentManifest, record, isUpgrade: true, oldManifest);
            Assert.True(consent.IsVerifiedUpgrade);
            PluginGrants.ApplyInstallGrants(record, consent);
        }
        else
        {
            store.Update(d => Assert.True(PluginGrants.TryMigrateLegacyRecord(d.Find("acme.sample")!, oldManifest, PluginSource.File)));
            store.Load();
            record = store.Current.Find("acme.sample")!;
        }

        var grants = new PluginGrants(currentManifest, record, builtin: false, PluginSource.File);
        var optionalB = currentManifest.Permissions.Requests.Single(r => r.Key == "fs.external" && r.Optional);
        var requiredA = currentManifest.Permissions.Requests.Single(r => r.Key == "fs.external" && !r.Optional);
        Assert.True(grants.IsGranted(optionalB));
        Assert.True(grants.Has("theme"));
        Assert.Equal(upgrade, grants.IsGranted(requiredA));
    }

    [Fact]
    public void Reordering_project_include_and_exclude_globs_preserves_denial()
    {
        var oldManifest = Parse("""{ "fs": { "project": { "access": "read", "include": ["src/**", "lib/**"], "exclude": ["**/dist/**", "**/tmp/**"], "optional": true } } }""").Manifest!;
        var reordered = Parse("""{ "fs": { "project": { "access": "read", "include": ["lib/**", "src/**"], "exclude": ["**/tmp/**", "**/dist/**"], "optional": true } } }""").Manifest!;
        var record = new PluginRecord { Source = PluginSource.File, Version = oldManifest.Version };

        PluginGrants.SetGrant(record, oldManifest, Assert.Single(oldManifest.Permissions.Requests), granted: false, PluginSource.File);
        Assert.True(new PluginGrants(reordered, record, builtin: false, PluginSource.File).IsDenied(Assert.Single(reordered.Permissions.Requests)));
    }

    [Theory]
    [InlineData("~/fixtures/a/**", true, false)]
    [InlineData("~/fixtures/b/**", true, true)]
    public void Old_settings_selected_required_external_read_survives_migration_and_disk_round_trip(string saved, bool first, bool second)
    {
        var manifest = Parse("""
            {
              "theme": true,
              "fs": {
                "external": [
                  { "path": "~/fixtures/a/**", "access": "read", "reason": "A" },
                  { "path": "~/fixtures/b/**", "access": "read", "reason": "B" }
                ]
              }
            }
            """).Manifest!;

        using var paths = new TempAppPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(Path.Combine(paths.DataDirectory, "plugins.json"), $$"""
            {
              "version": 1,
              "plugins": {
                "acme.sample": {
                  "enabled": true,
                  "source": "file",
                  "version": "1.0.0",
                  "grants": {
                    "theme": "granted",
                    "fs.external": "{{saved}}"
                  },
                  "denied": []
                }
              }
            }
            """);

        var store = new JsonPluginStateStore(paths);
        store.Load();
        store.Update(d => Assert.True(PluginGrants.TryMigrateLegacyRecord(d.Find("acme.sample")!, manifest, PluginSource.File)));
        store.Load();

        var record = store.Current.Find("acme.sample")!;
        Assert.False(PluginGrants.TryMigrateLegacyRecord(record, manifest, PluginSource.File));
        var grants = new PluginGrants(manifest, record, builtin: false, PluginSource.File);
        Assert.True(grants.Has("theme"));
        Assert.Equal([first, second], manifest.Permissions.Requests.Where(r => r.Key == "fs.external").Select(grants.IsGranted).ToArray());
    }

    [Fact]
    public void Old_settings_ambiguous_denied_or_mismatched_non_final_required_path_stays_conservative()
    {
        var ambiguousManifest = Parse("""
            {
              "fs": {
                "external": [
                  { "path": "~/fixtures/a/**", "access": "read", "reason": "A read" },
                  { "path": "~/fixtures/a/**", "access": "readwrite", "reason": "A write" },
                  { "path": "~/fixtures/b/**", "access": "read", "reason": "B read" }
                ]
              }
            }
            """).Manifest!;

        var ambiguousRecord = new PluginRecord
        {
            Enabled = true,
            Source = PluginSource.File,
            Version = "1.0.0",
            Grants = { ["fs.external"] = "~/fixtures/a/**" },
        };
        Assert.True(PluginGrants.TryMigrateLegacyRecord(ambiguousRecord, ambiguousManifest, PluginSource.File));
        var ambiguousGrants = new PluginGrants(ambiguousManifest, ambiguousRecord, builtin: false, PluginSource.File);
        Assert.Equal([true, false, false], ambiguousManifest.Permissions.Requests.Select(ambiguousGrants.IsGranted).ToArray());

        var deniedRecord = new PluginRecord
        {
            Enabled = true,
            Source = PluginSource.File,
            Version = "1.0.0",
            Grants = { ["fs.external"] = "~/fixtures/a/**" },
            Denied = { "fs.external" },
        };
        Assert.False(PluginGrants.TryMigrateLegacyRecord(deniedRecord, ambiguousManifest, PluginSource.File));
        var deniedGrants = new PluginGrants(ambiguousManifest, deniedRecord, builtin: false, PluginSource.File);
        Assert.All(ambiguousManifest.Permissions.Requests, r => Assert.False(deniedGrants.IsGranted(r)));

        var mismatchedRecord = new PluginRecord
        {
            Enabled = true,
            Source = PluginSource.File,
            Version = "0.9.0",
            Grants = { ["fs.external"] = "~/fixtures/a/**" },
        };
        Assert.False(PluginGrants.TryMigrateLegacyRecord(mismatchedRecord, ambiguousManifest, PluginSource.File));
        var mismatchedGrants = new PluginGrants(ambiguousManifest, mismatchedRecord, builtin: false, PluginSource.File);
        Assert.All(ambiguousManifest.Permissions.Requests, r => Assert.False(mismatchedGrants.IsGranted(r)));
    }
}
