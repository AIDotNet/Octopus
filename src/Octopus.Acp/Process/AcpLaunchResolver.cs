using System.Security.Cryptography;
using System.Text;
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
    AcpAdapterInstaller? adapterInstaller = null,
    IShellCommandBuilder? shellCommandBuilder = null)
{
    private readonly IShellCommandBuilder _shell = shellCommandBuilder ?? new ShellCommandBuilder();
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
            executable = ResolveExecutablePath(cmdExe, searchPath);
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

            // Point companion adapters at the configured command override (when set) or locally installed CLI binary
            // when declared (e.g. CLAUDE_CODE_EXECUTABLE). An explicit profile/ACP env var still wins.
            var envComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var explicitNativeEnv = !string.IsNullOrWhiteSpace(adapter.NativeExecutableEnvVar)
                && (profile.Env.Keys.Any(k => envComparer.Equals(k, adapter.NativeExecutableEnvVar))
                    || acp.Env.Keys.Any(k => envComparer.Equals(k, adapter.NativeExecutableEnvVar)));

            string? nativeExecutable = null;
            string? overriddenNativeLabel = null;
            var hasCommandOverride = false;
            if (!string.IsNullOrWhiteSpace(profile.Command))
            {
                var (cmdExe, cmdPrefix) = SplitOverrideOrDefault(profile.Command, "");
                if (!string.IsNullOrWhiteSpace(cmdExe))
                {
                    hasCommandOverride = true;
                    var resolvedNative = ResolveExecutablePath(cmdExe, searchPath);
                    overriddenNativeLabel = Path.GetFileName(resolvedNative);
                    EnsureDirectoryOnPath(env, Path.GetDirectoryName(resolvedNative));
                    nativeExecutable = cmdPrefix.Count > 0
                        ? EnsureNativeCommandWrapper(definition.Id, resolvedNative, cmdPrefix)
                        : resolvedNative;
                }
            }
            nativeExecutable ??= !string.IsNullOrWhiteSpace(installation?.Path) ? installation.Path : null;

            var appliedCommandOverride = false;
            if (!string.IsNullOrWhiteSpace(adapter.NativeExecutableEnvVar)
                && !string.IsNullOrWhiteSpace(nativeExecutable))
            {
                if (hasCommandOverride && !explicitNativeEnv)
                {
                    env[adapter.NativeExecutableEnvVar] = nativeExecutable;
                    appliedCommandOverride = true;
                }
                else if (!env.ContainsKey(adapter.NativeExecutableEnvVar))
                {
                    env[adapter.NativeExecutableEnvVar] = nativeExecutable;
                }
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
                var hasLocalNative = !string.IsNullOrWhiteSpace(nativeExecutable)
                    || (!string.IsNullOrWhiteSpace(adapter.NativeExecutableEnvVar) && env.ContainsKey(adapter.NativeExecutableEnvVar));
                var omitOptional = adapter.NpxOmitOptionalWhenInstalled && hasLocalNative;
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

            if (appliedCommandOverride && !string.IsNullOrWhiteSpace(overriddenNativeLabel))
            {
                sourceLabel = $"{sourceLabel} → {overriddenNativeLabel}";
            }
        }

        // Ensure the directory of the resolved binary is on PATH (nvm/fnm/volta scripts using #!/usr/bin/env node).
        EnsureDirectoryOnPath(env, Path.GetDirectoryName(executable));

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

    private (string Exe, List<string> Prefix) SplitOverrideOrDefault(string? commandOverride, string fallbackExe)
    {
        if (string.IsNullOrWhiteSpace(commandOverride))
        {
            return (fallbackExe, []);
        }
        var parts = _shell.Split(commandOverride);
        return parts.Count == 0 ? (fallbackExe, []) : (parts[0], parts.Skip(1).ToList());
    }

    private string ResolveExecutablePath(string exe, string searchPath)
    {
        if (string.IsNullOrWhiteSpace(exe))
        {
            return exe;
        }
        var expanded = ExpandHome(exe);
        var found = locator.FindAll(expanded, searchPath);
        return found.Count > 0 ? found[0] : expanded;
    }

    private static string ExpandHome(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                return AgentSearchPaths.Expand(trimmed, home, Environment.GetEnvironmentVariable);
            }
        }
        return trimmed;
    }

    private static void EnsureDirectoryOnPath(Dictionary<string, string> env, string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }
        var curPath = env.GetValueOrDefault("PATH") ?? "";
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!curPath.Split(Path.PathSeparator).Contains(directory, comparer))
        {
            env["PATH"] = string.IsNullOrEmpty(curPath) ? directory : $"{directory}{Path.PathSeparator}{curPath}";
        }
    }

    private string EnsureNativeCommandWrapper(string agentId, string resolvedExecutable, IReadOnlyList<string> prefixArgs)
    {
        if (OperatingSystem.IsWindows() || adapterInstaller is null || prefixArgs.Count == 0)
        {
            return resolvedExecutable;
        }
        try
        {
            var execLine = _shell.Build(resolvedExecutable, prefixArgs, ShellKind.Posix);
            var script = $"#!/bin/sh\nexec {execLine} \"$@\"\n";
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..16];
            var dir = Path.Combine(adapterInstaller.AdapterRoot(agentId), "wrappers");
            Directory.CreateDirectory(dir);
            var wrapperPath = Path.Combine(dir, $"cmd-{hash}.sh");
            if (!File.Exists(wrapperPath))
            {
                var tempPath = Path.Combine(dir, $"cmd-{hash}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tempPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.SetUnixFileMode(tempPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(tempPath, wrapperPath, overwrite: true);
            }
            return wrapperPath;
        }
        catch
        {
            return resolvedExecutable;
        }
    }
}
