using Octopus.Agents;
using Octopus.Core.Agents;
using Octopus.Core.Settings;
using Octopus.Platform;

namespace Octopus.Acp.Process;

/// <summary>Resolved executable, arguments, environment and dialect for starting an ACP child process.</summary>
public sealed record AcpLaunchPlan(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> UnsetEnvironment,
    string WorkingDirectory,
    string DialectId,
    AcpPermissionStrategy PermissionStrategy,
    string SourceLabel,
    string? AgentVersion = null,
    TimeSpan? InitializeTimeout = null);

/// <summary>Resolves how to launch an agent in ACP mode (native CLI, local/cached adapter binary, or managed install / runner).</summary>
public sealed class AcpLaunchResolver(
    IExecutableLocator locator,
    ILoginEnvironment loginEnvironment,
    ISystemProxy? systemProxy = null,
    AcpAdapterInstaller? adapterInstaller = null)
{
    public bool CanLaunchAcp(AgentDefinition? definition, AgentInstallation? installation, AgentSettings settings)
    {
        if (definition?.Acp is not { } acp) return false;
        if (string.Equals(acp.Mode, "native", StringComparison.OrdinalIgnoreCase))
        {
            return installation is not null || !string.IsNullOrWhiteSpace(settings.ProfileOf(definition.Id).Command);
        }
        // Adapter mode: available if a local or cached adapter exists, or a runner / registry download is allowed.
        if (acp.Adapter is not { } adapter) return false;
        var searchPath = GetSearchPathSync();
        if (FindAnyExecutable(adapter.Executables, searchPath) is not null) return true;
        if (adapterInstaller?.FindCachedExecutable(definition.Id, adapter) is not null) return true;
        if (settings.AcpAdapterPolicy != AcpAdapterPolicy.NativeOnly)
        {
            if (settings.AcpAdapterPolicy == AcpAdapterPolicy.ManagedRegistry && !string.IsNullOrWhiteSpace(adapter.RegistryId)) return true;
            if (!string.IsNullOrWhiteSpace(adapter.NpxPackage)
                && (FindAnyExecutable(["npm", "npx"], searchPath) is not null)) return true;
            if (!string.IsNullOrWhiteSpace(adapter.UvxPackage) && FindAnyExecutable(["uvx"], searchPath) is not null) return true;
        }
        return false;
    }

    public async Task<AcpLaunchPlan> ResolveAsync(
        AgentDefinition definition,
        AgentInstallation? installation,
        AgentSettings settings,
        string workingDirectory,
        string effectivePermissionMode,
        IReadOnlyDictionary<string, string>? accountEnv = null,
        IReadOnlyList<string>? unsetEnv = null,
        CancellationToken cancellationToken = default,
        Action<string>? onProgress = null)
    {
        if (definition.Acp is not { } acp)
        {
            throw new InvalidOperationException($"Agent '{definition.Id}' does not support ACP.");
        }

        var loginEnv = await loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        var env = new Dictionary<string, string>(loginEnv,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var searchPath = BuildSearchPath(env, definition.Detect.ExtraDirs, installation?.Path);
        if (!string.IsNullOrEmpty(searchPath))
        {
            env["PATH"] = searchPath;
        }

        var profile = settings.ProfileOf(definition.Id);
        foreach (var (k, v) in definition.Capabilities.BaseEnv) env[k] = v;
        foreach (var (k, v) in profile.Env) env[k] = v;
        if (accountEnv is not null)
        {
            foreach (var (k, v) in accountEnv) env[k] = v;
        }
        if (unsetEnv is not null)
        {
            foreach (var k in unsetEnv) env.Remove(k);
        }
        if (settings.UseSystemProxy && systemProxy?.Resolve() is { } proxy)
        {
            foreach (var (k, v) in AgentProxyEnvironment.Compute(proxy, env))
            {
                env[k] = v;
            }
        }
        foreach (var (k, v) in acp.Env) env[k] = v;

        string executable;
        var args = new List<string>();
        string sourceLabel;
        var initTimeout = TimeSpan.FromSeconds(60);

        if (string.Equals(acp.Mode, "native", StringComparison.OrdinalIgnoreCase))
        {
            var (cmdExe, cmdPrefix) = SplitOverrideOrDefault(profile.Command, installation?.Path ?? definition.Detect.Executables.FirstOrDefault() ?? definition.Id);
            executable = cmdExe;
            args.AddRange(cmdPrefix);
            args.AddRange(acp.Args);
            if (effectivePermissionMode == PermissionModes.Full && acp.FullAccessArgs.Count > 0)
            {
                // Insert full-access flags before trailing subcommand like "stdio" when present (e.g. grok agent --always-approve stdio).
                var stdioIdx = args.IndexOf("stdio");
                if (stdioIdx >= 0) args.InsertRange(stdioIdx, acp.FullAccessArgs);
                else args.AddRange(acp.FullAccessArgs);
            }
            sourceLabel = $"{Path.GetFileName(executable)} {string.Join(' ', args)}".Trim();
        }
        else
        {
            var adapter = acp.Adapter ?? throw new InvalidOperationException($"ACP adapter spec missing for '{definition.Id}'.");

            // Point companion adapters at the locally installed CLI binary when declared (e.g. CLAUDE_CODE_EXECUTABLE).
            if (!string.IsNullOrWhiteSpace(adapter.NativeExecutableEnvVar)
                && !string.IsNullOrWhiteSpace(installation?.Path)
                && !env.ContainsKey(adapter.NativeExecutableEnvVar))
            {
                env[adapter.NativeExecutableEnvVar] = installation.Path;
            }

            if (FindAnyExecutable(adapter.Executables, searchPath) is { } localAdapter)
            {
                executable = localAdapter;
                args.AddRange(adapter.Args);
                sourceLabel = Path.GetFileName(localAdapter);
            }
            else if (adapterInstaller?.FindCachedExecutable(definition.Id, adapter) is { } cachedAdapter)
            {
                executable = cachedAdapter;
                args.AddRange(adapter.Args);
                sourceLabel = $"{Path.GetFileName(cachedAdapter)} (cached)";
            }
            else if (settings.AcpAdapterPolicy == AcpAdapterPolicy.ManagedRegistry
                && adapterInstaller is not null
                && await TryInstallRegistryBinaryQuietlyAsync(definition.Id, adapter, onProgress, cancellationToken).ConfigureAwait(false) is { } downloadedBin)
            {
                executable = downloadedBin;
                args.AddRange(adapter.Args);
                sourceLabel = $"{Path.GetFileName(downloadedBin)} (registry)";
            }
            else if (settings.AcpAdapterPolicy != AcpAdapterPolicy.NativeOnly
                && adapterInstaller is not null
                && !string.IsNullOrWhiteSpace(adapter.NpxPackage)
                && FindAnyExecutable(["npm"], searchPath) is { } npm)
            {
                // Atomic managed install into Octopus cache: skips redundant 364MB optional CLI binaries when local CLI exists
                // and avoids poisoning global ~/.npm/_npx if cancelled.
                var omitOptional = adapter.NpxOmitOptionalWhenInstalled && !string.IsNullOrWhiteSpace(installation?.Path);
                executable = await adapterInstaller.EnsureNpmAdapterAsync(
                    definition.Id,
                    adapter,
                    npm,
                    env,
                    omitOptional,
                    onProgress,
                    cancellationToken).ConfigureAwait(false);
                args.AddRange(adapter.NpxArgs.Count > 0 ? adapter.NpxArgs : adapter.Args);
                sourceLabel = $"{Path.GetFileName(executable)} (managed)";
            }
            else if (settings.AcpAdapterPolicy != AcpAdapterPolicy.NativeOnly
                && !string.IsNullOrWhiteSpace(adapter.NpxPackage)
                && FindAnyExecutable(["npx"], searchPath) is { } npx)
            {
                executable = npx;
                args.Add("-y");
                args.Add(adapter.NpxPackage);
                args.AddRange(adapter.NpxArgs);
                sourceLabel = $"npx {adapter.NpxPackage}";
                initTimeout = TimeSpan.FromSeconds(180);
            }
            else if (settings.AcpAdapterPolicy != AcpAdapterPolicy.NativeOnly
                && !string.IsNullOrWhiteSpace(adapter.UvxPackage)
                && FindAnyExecutable(["uvx"], searchPath) is { } uvx)
            {
                executable = uvx;
                args.Add(adapter.UvxPackage);
                args.AddRange(adapter.UvxArgs);
                sourceLabel = $"uvx {adapter.UvxPackage}";
                initTimeout = TimeSpan.FromSeconds(180);
            }
            else
            {
                throw new InvalidOperationException(
                    $"No ACP adapter found for '{definition.Name}'. Install {adapter.Executables.FirstOrDefault() ?? adapter.NpxPackage ?? definition.Id} or enable local npm/npx runners in Settings.");
            }
        }

        // Ensure the directory of the resolved binary is on PATH (nvm/fnm/volta scripts using #!/usr/bin/env node).
        var exeDir = Path.GetDirectoryName(executable);
        if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir))
        {
            var curPath = env.GetValueOrDefault("PATH") ?? "";
            if (!curPath.Split(Path.PathSeparator).Contains(exeDir, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                env["PATH"] = string.IsNullOrEmpty(curPath) ? exeDir : $"{exeDir}{Path.PathSeparator}{curPath}";
            }
        }

        return new AcpLaunchPlan(
            executable,
            args,
            env,
            unsetEnv ?? [],
            workingDirectory,
            acp.Dialect,
            acp.PermissionStrategy,
            sourceLabel,
            installation?.Version,
            initTimeout);
    }

    private async Task<string?> TryInstallRegistryBinaryQuietlyAsync(
        string agentId,
        AgentAcpAdapterSpec adapter,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        if (adapterInstaller is null) return null;
        try
        {
            return await adapterInstaller.TryEnsureRegistryBinaryAsync(agentId, adapter, onProgress, cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Fall through to npm/npx if registry binary download fails or is unavailable for this host.
            return null;
        }
    }

    private string? FindAnyExecutable(IReadOnlyList<string> names, string? searchPath)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var found = locator.FindAll(name, searchPath);
            if (found.Count > 0) return found[0];
        }
        return null;
    }

    private string? GetSearchPathSync()
    {
        string? rawPath = null;
        if (loginEnvironment.GetAsync() is { IsCompletedSuccessfully: true } ready
            && ready.Result.TryGetValue("PATH", out var p))
        {
            rawPath = p;
        }
        else
        {
            rawPath = Environment.GetEnvironmentVariable("PATH");
        }
        return CombinePaths(rawPath, AgentSearchPaths.ForThisMachine());
    }

    private static string BuildSearchPath(IReadOnlyDictionary<string, string> env, IReadOnlyList<string> extraDirs, string? installedPath = null)
    {
        var basePath = env.FirstOrDefault(kv => string.Equals(kv.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;
        var extras = new List<string>(AgentSearchPaths.ForThisMachine());
        if (!string.IsNullOrWhiteSpace(installedPath))
        {
            var dir = Path.GetDirectoryName(installedPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) extras.Insert(0, dir);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in extraDirs)
        {
            var expanded = AgentSearchPaths.Expand(dir, home, Environment.GetEnvironmentVariable);
            if (expanded.Length > 0 && Directory.Exists(expanded)) extras.Add(expanded);
        }
        return CombinePaths(basePath, extras) ?? "";
    }

    private static string? CombinePaths(string? path, IReadOnlyList<string> extras)
    {
        if (extras.Count == 0) return path;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>((path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries), comparer);
        var added = extras.Where(d => seen.Add(d)).ToList();
        return added.Count == 0 ? path : string.Join(Path.PathSeparator, new[] { path ?? "" }.Concat(added).Where(s => s.Length > 0));
    }

    private static (string Exe, List<string> Prefix) SplitOverrideOrDefault(string? commandOverride, string fallbackExe)
    {
        if (string.IsNullOrWhiteSpace(commandOverride))
        {
            return (fallbackExe, []);
        }
        var parts = commandOverride.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? (fallbackExe, []) : (parts[0], parts.Skip(1).ToList());
    }
}
