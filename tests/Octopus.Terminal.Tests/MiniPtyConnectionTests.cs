using System.Text;
using Octopus.Core.Terminals;
using Octopus.Terminal.MiniPty;
using Xunit;

namespace Octopus.Terminal.Tests;

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) Skip = "Unix only";
    }
}

/// <summary>Real ptys: proves MiniPty (net10 assets) works on net11.</summary>
public class MiniPtyConnectionTests
{
    private static readonly Dictionary<string, string> BaseEnv = new()
    {
        ["PATH"] = "/usr/bin:/bin",
        ["TERM"] = "xterm-256color",
    };

    private static LaunchSpec Spec(string file, string[] args, string? cwd = null, Dictionary<string, string>? env = null) =>
        new(file, args, cwd, env ?? BaseEnv);

    private static Task<StringBuilder> Collect(ITerminalConnection c, StringBuilder sb) => Task.Run(async () =>
    {
        await foreach (var chunk in c.ReadOutputAsync())
            lock (sb) sb.Append(Encoding.UTF8.GetString(chunk.Span));
        return sb;
    });

    private static async Task WaitFor(StringBuilder sb, string needle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            lock (sb) if (sb.ToString().Contains(needle)) return;
            await Task.Delay(20);
        }
        lock (sb) Assert.Fail($"Timed out waiting for '{needle}'; got: {sb}");
    }

    [UnixFact]
    public async Task Output_and_exit_code()
    {
        await using var c = new MiniPtyConnectionFactory().Start(Spec("/bin/sh", ["-c", "printf hello; exit 3"]), 80, 24);
        var sb = new StringBuilder();
        var read = Collect(c, sb);
        var exit = await c.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, exit.ExitCode);
        // Exited completes once the output was read to the end; the reader's own loop may still be a moment from returning.
        await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("hello", sb.ToString());
    }

    [UnixFact]
    public async Task Cat_echoes_and_interrupt_ends_it()
    {
        await using var c = new MiniPtyConnectionFactory().Start(Spec("/bin/cat", []), 80, 24);
        var sb = new StringBuilder();
        _ = Collect(c, sb);
        await c.WriteAsync("ping\n"u8.ToArray());
        await WaitFor(sb, "ping");
        await c.ResizeAsync(100, 30, 800, 600);
        await c.SignalAsync(TerminalSignal.Interrupt);
        var exit = await c.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(exit.ExitCode is not 0 || exit.Signal is not null);
    }

    [UnixFact]
    public async Task Resize_is_visible_to_child()
    {
        await using var c = new MiniPtyConnectionFactory().Start(Spec("/bin/sh", ["-c", "read x; stty size"]), 80, 24);
        var sb = new StringBuilder();
        _ = Collect(c, sb);
        await c.ResizeAsync(100, 30, 0, 0);
        await c.WriteAsync("\n"u8.ToArray());
        await WaitFor(sb, "30 100");
    }

    [UnixFact]
    public async Task Environment_is_exactly_the_spec()
    {
        Environment.SetEnvironmentVariable("OCTOPUS_PARENT_ONLY", "leak");
        var env = new Dictionary<string, string>(BaseEnv) { ["FOO"] = "bar" };
        await using var c = new MiniPtyConnectionFactory().Start(
            Spec("/bin/sh", ["-c", "printf '[%s][%s][%s]' \"$FOO\" \"$OCTOPUS_PARENT_ONLY\" \"$TERM\""], env: env), 80, 24);
        var sb = new StringBuilder();
        var read = Collect(c, sb);
        await c.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("[bar][][xterm-256color]", sb.ToString());
    }

    [UnixFact]
    public async Task Working_directory_is_applied()
    {
        var dir = Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd('/'), true)?.FullName ?? Path.GetTempPath().TrimEnd('/');
        await using var c = new MiniPtyConnectionFactory().Start(Spec("/bin/sh", ["-c", "pwd -P"], cwd: dir), 80, 24);
        var sb = new StringBuilder();
        _ = Collect(c, sb);
        await c.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(Path.GetFileName(dir), sb.ToString());
    }

    [Fact]
    public async Task Dispose_ends_running_process()
    {
        var spec = OperatingSystem.IsWindows()
            ? Spec(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoLogo", "-NoProfile", "-Command", "Start-Sleep -Seconds 60"], env: [])
            : Spec("/bin/sleep", ["60"]);
        var c = new MiniPtyConnectionFactory().Start(spec, 80, 24);
        using var process = System.Diagnostics.Process.GetProcessById(c.ProcessId);
        _ = Collect(c, new StringBuilder());
        await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
        Assert.True(c.Exited.IsCompleted);
        Assert.True(process.HasExited);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_powershell_accepts_input_resize_and_reports_exit(bool defaultShell)
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = defaultShell
            ? new Octopus.Platform.Windows.WindowsShellResolver(new Octopus.Platform.Windows.WindowsExecutableLocator()).GetDefaultShell().Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            env[(string)entry.Key] = (string?)entry.Value ?? "";
        await using var connection = new MiniPtyConnectionFactory().Start(
            Spec(shell, ["-NoLogo", "-NoProfile"], env: env), 80, 24);
        var output = new StringBuilder();
        var reading = Collect(connection, output);
        await WaitFor(output, ">");
        await connection.ResizeAsync(100, 30, 0, 0);
        await connection.WriteAsync(Encoding.UTF8.GetBytes("[Console]::WriteLine(('WINDOW_{0}_{1}' -f [Console]::WindowWidth, [Console]::WindowHeight))\r"));
        await WaitFor(output, "WINDOW_100_30");
        await connection.WriteAsync("exit 7\r"u8.ToArray());
        var exit = await connection.Exited.WaitAsync(TimeSpan.FromSeconds(15));
        await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, exit.ExitCode);
    }

    [Fact]
    public async Task Windows_cmd_agent_command_preserves_quoted_executable_and_arguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("octopus terminal ");
        try
        {
            var file = Path.Combine(directory.FullName, "agent shim.cmd");
            await File.WriteAllTextAsync(file, "@echo off\r\necho OCTOPUS_QUOTED_PATH_OK\r\necho [%1]\r\n");
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var command = new Octopus.Platform.ShellCommandBuilder().Build(file, ["hello world"], Octopus.Platform.ShellKind.Cmd);
            await using var connection = new MiniPtyConnectionFactory().Start(
                Spec(shell, ["/d", "/s", "/e:on", "/v:off", "/c", command], env: []), 80, 24);
            var output = new StringBuilder();
            var reading = Collect(connection, output);
            var exit = await connection.Exited.WaitAsync(TimeSpan.FromSeconds(15));
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exit.ExitCode);
            Assert.Contains("OCTOPUS_QUOTED_PATH_OK", output.ToString());
            Assert.Contains("[\"hello world\"]", output.ToString());
        }
        finally { directory.Delete(true); }
    }
}
