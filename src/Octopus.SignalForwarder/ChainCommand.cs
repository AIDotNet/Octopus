using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Octopus.SignalForwarder;

/// <summary>
/// The user's own command for a slot that holds only one (Claude's status line, Codex's notify), resolved by Octopus
/// when it launched the agent and passed as <c>OCTOPUS_SIGNAL_CHAIN</c>: <c>{"shell":"…"}</c> runs through the
/// platform shell like the agent would, <c>{"argv":["prog","arg"]}</c> runs directly.
/// </summary>
internal sealed record ChainCommand(string? Shell, IReadOnlyList<string>? Argv)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Null when unset or not understood: the forwarder then behaves as if the user had no command.</summary>
    public static ChainCommand? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (root.TryGetProperty("shell", out var shell) && shell.ValueKind == JsonValueKind.String && shell.GetString() is { Length: > 0 } command)
            {
                return new ChainCommand(command, null);
            }
            if (root.TryGetProperty("argv", out var argv) && argv.ValueKind == JsonValueKind.Array)
            {
                var list = argv.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList();
                return list.Count > 0 && list[0].Length > 0 ? new ChainCommand(null, list) : null;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>
    /// Runs the command with <paramref name="stdin"/> on its standard input and <paramref name="extraArgument"/>
    /// appended, and returns its exit code and standard output. Variables meant only for the forwarder are removed
    /// from its environment.
    /// </summary>
    public async Task<(int ExitCode, byte[] Output)> RunAsync(string? stdin, string? extraArgument, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
        };
        if (Shell is not null)
        {
            var command = extraArgument is null ? Shell : $"{Shell} {QuoteForShell(extraArgument)}";
            if (OperatingSystem.IsWindows())
            {
                start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/s");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add(command);
            }
            else
            {
                start.FileName = "/bin/sh";
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(command);
            }
        }
        else
        {
            start.FileName = Argv![0];
            foreach (var arg in Argv.Skip(1))
            {
                start.ArgumentList.Add(arg);
            }
            if (extraArgument is not null)
            {
                start.ArgumentList.Add(extraArgument);
            }
        }
        start.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            if (key is not (SignalEnvironment.TokenVariable or SignalEnvironment.ChainVariable or SignalEnvironment.EndpointVariable))
            {
                start.Environment[key] = value;
            }
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("the chained command did not start");
        var output = new MemoryStream();
        var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(stdin));
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command exited without reading its input.
        }
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            await reading;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (1, output.ToArray());
        }
        return (process.ExitCode, output.ToArray());
    }

    private static string QuoteForShell(string value) => OperatingSystem.IsWindows()
        ? "\"" + value.Replace("\"", "\\\"") + "\""
        : "'" + value.Replace("'", "'\\''") + "'";
}
