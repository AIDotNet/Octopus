using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Octopus.Core.Projects;
using Octopus.Platform;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>octopus.process.run</c> (high risk, design/plugins/04-权限与文件系统.md §8): only programs the manifest lists, found
/// on the login PATH, started without a shell, with a time limit and capped output. When <c>options.device</c> targets an
/// SSH or remote Octopus device, <c>process.run.devices</c> must also allow that device scope.
/// </summary>
public sealed class ProcessModule(IExecutableLocator locator, ILoginEnvironment login, IPluginSystemService? system = null) : IPluginApiModule
{
    public const int MaxOutputBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Decides whether the plugin may run a program in a directory; null allows only its own data directory.</summary>
    public Func<PluginHost, string, bool>? WorkingDirectoryCheck { get; set; }

    public void Register(PluginApiRouter router) => router.Request("process.run", RunAsync);

    private async Task<PluginReply> RunAsync(PluginCall call, CancellationToken cancellationToken)
    {
        var grants = call.Plugin.Grants;
        grants.Require("process.run");
        var command = call.RequiredString("command");
        if (command.IndexOfAny(['/', '\\']) >= 0 || !grants.Details("process.run").Contains(command, StringComparer.Ordinal))
        {
            throw PluginApiException.Denied($"process.run:{command}", canRequest: false);
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(call.Long("timeoutMs") ?? (long)DefaultTimeout.TotalMilliseconds, 100, (long)MaxTimeout.TotalMilliseconds));
        var rawDevice = call.String("device");
        var deviceKey = string.IsNullOrWhiteSpace(rawDevice) ? DeviceKeys.Local : DeviceKeys.Normalize(rawDevice);
        if (!string.Equals(deviceKey, DeviceKeys.Local, StringComparison.Ordinal))
        {
            return await RunRemoteDeviceAsync(call, deviceKey, command, timeout, cancellationToken).ConfigureAwait(false);
        }

        var cwd = call.String("cwd") ?? call.Plugin.DataDirectory;
        var allowed = cwd == call.Plugin.DataDirectory || (WorkingDirectoryCheck?.Invoke(call.Plugin, cwd) ?? false);
        if (!allowed)
        {
            throw PluginApiException.Denied("fs", canRequest: false, path: cwd);
        }
        Directory.CreateDirectory(call.Plugin.DataDirectory);
        if (!Directory.Exists(cwd))
        {
            throw new PluginApiException("NotFound", $"{cwd} does not exist.") { Path = cwd };
        }

        var environment = await login.GetAsync(cancellationToken).ConfigureAwait(false);
        var executable = locator.FindAll(command, environment.GetValueOrDefault("PATH")).FirstOrDefault()
            ?? throw new PluginApiException("NotFound", $"{command} is not installed (not on PATH).");

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in call.Strings("args")) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            if (!key.StartsWith("OCTOPUS_", StringComparison.Ordinal)) start.Environment[key] = value;
        }
        if (call["env"] is JsonObject extra)
        {
            foreach (var (key, value) in extra)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && !key.StartsWith("OCTOPUS_", StringComparison.Ordinal)) start.Environment[key] = s;
            }
        }

        using var process = Process.Start(start) ?? throw new PluginApiException("Io", $"{command} did not start.");
        var stdout = ReadCappedAsync(process.StandardOutput.BaseStream);
        var stderr = ReadCappedAsync(process.StandardError.BaseStream);
        try
        {
            if (call.Text.Length > 0) await process.StandardInput.WriteAsync(call.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new PluginApiException("Timeout", $"{command} did not finish within {timeout.TotalSeconds:0} s.");
        }
        return PluginReply.Of(new JsonObject
        {
            ["exitCode"] = process.ExitCode,
            ["stdout"] = await stdout.ConfigureAwait(false),
            ["stderr"] = await stderr.ConfigureAwait(false),
        });
    }

    private async Task<PluginReply> RunRemoteDeviceAsync(PluginCall call, string deviceKey, string command, TimeSpan timeout, CancellationToken ct)
    {
        if (system is null) throw PluginApiException.Unsupported("Multi-device process execution");
        var grants = call.Plugin.Grants;
        grants.Require("process.run.devices");
        if (!DeviceAllowed(deviceKey, grants.Details("process.run.devices")))
        {
            throw PluginApiException.Denied($"process.run.devices:{deviceKey}", canRequest: false);
        }

        Dictionary<string, string>? env = null;
        if (call["env"] is JsonObject extra)
        {
            env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in extra)
            {
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && !k.StartsWith("OCTOPUS_", StringComparison.Ordinal))
                {
                    env[k] = s;
                }
            }
        }

        try
        {
            var result = await system.RunOnDeviceAsync(
                deviceKey,
                command,
                call.Strings("args"),
                call.String("cwd"),
                env,
                call.Text.Length > 0 ? call.Text : null,
                timeout,
                ct).ConfigureAwait(false);
            if (result.TimedOut)
            {
                throw new PluginApiException("Timeout", $"{command} on {deviceKey} did not finish within {timeout.TotalSeconds:0} s.");
            }
            call.Plugin.Write(PluginLogLevel.Info, $"process.run: device={deviceKey} command={command} exit={result.ExitCode}");
            return PluginReply.Of(new JsonObject
            {
                ["exitCode"] = result.ExitCode,
                ["stdout"] = result.StandardOutput,
                ["stderr"] = result.StandardError,
            });
        }
        catch (NotSupportedException e)
        {
            throw PluginApiException.Unsupported(e.Message);
        }
        catch (Exception e) when (e is not PluginApiException and not OperationCanceledException)
        {
            throw new PluginApiException("Io", e.Message);
        }
    }

    internal static bool DeviceAllowed(string deviceKey, IReadOnlyList<string> scopes)
    {
        var (kind, _) = DeviceKeys.Parse(deviceKey);
        foreach (var raw in scopes)
        {
            var scope = raw.Trim();
            if (scope == "*" || string.Equals(scope, deviceKey, StringComparison.Ordinal)) return true;
            if (kind == ProjectLocationKind.Ssh && scope is "ssh" or "ssh:*") return true;
            if (kind == ProjectLocationKind.Remote && scope is "remote" or "remote:*") return true;
            if (kind == ProjectLocationKind.Local && scope.Equals("local", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<string> ReadCappedAsync(Stream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length < MaxOutputBytes) buffer.Write(chunk, 0, (int)Math.Min(read, MaxOutputBytes - buffer.Length));
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
