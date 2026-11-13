using System.Net.Sockets;
using Octopus.Platform;

namespace Octopus.Ssh;

/// <summary>What a test of a host found.</summary>
public enum SshTestOutcome
{
    /// <summary>The host answered and, for ssh and mosh, let the test in.</summary>
    Connected,
    /// <summary>The host answered, but a password, a passphrase or another key is needed: the test cannot answer those, the terminal can.</summary>
    AuthenticationNeeded,
    /// <summary>A mosh host whose server has no <c>mosh-server</c> program installed.</summary>
    MoshServerMissing,
    /// <summary>The host could not be reached, or refused the connection.</summary>
    Failed,
}

/// <param name="Outcome">What was found.</param>
/// <param name="Detail">What the program said (in its own language), or empty; shown under the outcome.</param>
public sealed record SshTestResult(SshTestOutcome Outcome, string Detail = "");

/// <summary>Tries a host as it is typed in the form, before it is saved.</summary>
public interface ISshHostTester
{
    Task<SshTestResult> TestAsync(SshHostDraft draft, CancellationToken cancellationToken = default);
}

/// <summary>
/// ssh and mosh hosts: one <c>ssh</c> run of a command that does nothing (for mosh, one that looks for
/// <c>mosh-server</c>), with every field given on the command line, so that a host that is not in the file yet can be
/// tried. The run never asks anything (a password cannot be typed) and never writes to the known hosts. Telnet hosts:
/// a TCP connection to the port.
/// </summary>
public sealed class SshHostTester(IProcessRunner runner) : ISshHostTester
{
    /// <summary>How long a test waits for the host.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The port telnet uses when the host sets none.</summary>
    public const int TelnetPort = 23;

    public async Task<SshTestResult> TestAsync(SshHostDraft draft, CancellationToken cancellationToken = default)
    {
        try
        {
            return draft.Protocol == SshHostProtocol.Telnet
                ? await TestTelnetAsync(draft, cancellationToken)
                : await TestSshAsync(draft, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SshTestResult(SshTestOutcome.Failed, "Timed out");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // ssh is not installed, or the program cannot be started.
            return new SshTestResult(SshTestOutcome.Failed, e.Message);
        }
    }

    /// <summary>The arguments of the ssh run that tests a host, as they are given to the program (nothing is quoted for a shell).</summary>
    public static IReadOnlyList<string> Arguments(SshHostDraft draft)
    {
        var args = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=" + (int)Timeout.TotalSeconds,
            // A test must leave nothing behind: a host key that is not known yet is not written to the known hosts.
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            "-o", "LogLevel=ERROR",
            "-o", "ControlMaster=no",
            "-o", "ControlPath=none",
        };
        AddOption(args, "-p", draft.Port);
        AddOption(args, "-l", draft.User);
        AddOption(args, "-i", draft.IdentityFile);
        AddOption(args, "-J", draft.ProxyJump);
        var host = string.IsNullOrWhiteSpace(draft.HostName) ? draft.Alias.Trim() : draft.HostName.Trim();
        // mosh starts its server through ssh, and needs the program on the host.
        args.AddRange(["--", host, draft.Protocol == SshHostProtocol.Mosh ? "command -v mosh-server" : "exit 0"]);
        return args;
    }

    private static void AddOption(List<string> args, string flag, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.AddRange([flag, value.Trim()]);
        }
    }

    private async Task<SshTestResult> TestSshAsync(SshHostDraft draft, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("ssh", Arguments(draft), timeout: Timeout + TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
        if (result.TimedOut)
        {
            return new SshTestResult(SshTestOutcome.Failed, "Timed out");
        }
        var detail = LastLine(result.StandardError);
        if (result.ExitCode == 0)
        {
            return new SshTestResult(SshTestOutcome.Connected);
        }
        if (detail.Contains("Permission denied", StringComparison.Ordinal))
        {
            return new SshTestResult(SshTestOutcome.AuthenticationNeeded, detail);
        }
        // `command -v` says nothing and exits 1 when the program is not there; a failed connection exits 255.
        if (draft.Protocol == SshHostProtocol.Mosh && result.ExitCode == 1 && detail.Length == 0)
        {
            return new SshTestResult(SshTestOutcome.MoshServerMissing);
        }
        return new SshTestResult(SshTestOutcome.Failed, detail.Length > 0 ? detail : $"ssh exited with {result.ExitCode}");
    }

    private static async Task<SshTestResult> TestTelnetAsync(SshHostDraft draft, CancellationToken cancellationToken)
    {
        var host = string.IsNullOrWhiteSpace(draft.HostName) ? draft.Alias.Trim() : draft.HostName.Trim();
        var port = int.TryParse(draft.Port, out var typed) ? typed : TelnetPort;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, limit.Token);
            return new SshTestResult(SshTestOutcome.Connected);
        }
        catch (SocketException e)
        {
            return new SshTestResult(SshTestOutcome.Failed, e.Message);
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
