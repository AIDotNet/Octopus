using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Octopus.SignalForwarder.Tests;

public class ForwarderOptionsTests
{
    [Fact]
    public void Parses_each_mode_with_agent_and_channel()
    {
        var statusLine = ForwarderOptions.Parse(["statusline", "--agent", "claude", "--channel", "statusline", "--min-interval", "2000"]);
        Assert.Equal(new ForwarderOptions(ForwarderMode.StatusLine, "claude", "statusline", 2000, null, null), statusLine);

        var hook = ForwarderOptions.Parse(["hook", "--agent", "cursor", "--channel", "stop", "--reply", "{}"]);
        Assert.Equal(ForwarderMode.Hook, hook!.Mode);
        Assert.Equal("{}", hook.Reply);
    }

    [Fact]
    public void Codex_notify_payload_is_the_trailing_argument()
    {
        var notify = ForwarderOptions.Parse(["notify", "--agent", "codex", "--channel", "turn-complete", "{\"type\":\"agent-turn-complete\"}"]);
        Assert.Equal(ForwarderMode.Notify, notify!.Mode);
        Assert.Equal("{\"type\":\"agent-turn-complete\"}", notify.ArgumentPayload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown --agent claude --channel x")]
    [InlineData("hook --channel stop")]
    [InlineData("hook --agent claude")]
    [InlineData("hook --agent claude --channel")]
    public void Rejects_incomplete_command_lines(string commandLine) =>
        Assert.Null(ForwarderOptions.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
}

public class SignalEnvelopeTests
{
    [Fact]
    public void Carries_the_payload_as_a_string_and_the_account_the_agent_sees()
    {
        var environment = SignalEnvironment.From(new Dictionary<string, string>
        {
            [SignalEnvironment.TokenVariable] = "tok",
            [SignalEnvironment.ItemVariable] = "item-1",
            [SignalEnvironment.AccountVariableName] = "CLAUDE_CONFIG_DIR",
            ["CLAUDE_CONFIG_DIR"] = "/Users/张三/.claude-work",
        });
        var options = ForwarderOptions.Parse(["statusline", "--agent", "claude", "--channel", "statusline"])!;
        var payload = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":42}},\"text\":\"引号\\\" 和换行\\n\"}";

        using var envelope = JsonDocument.Parse(SignalEnvelope.Build(options, environment, payload, 1794069588307));
        var root = envelope.RootElement;

        Assert.Equal(SignalEnvelope.Version, root.GetProperty("v").GetInt32());
        Assert.Equal("tok", root.GetProperty("token").GetString());
        Assert.Equal("item-1", root.GetProperty("item").GetString());
        Assert.Equal("claude", root.GetProperty("agent").GetString());
        Assert.Equal("statusline", root.GetProperty("channel").GetString());
        Assert.Equal("/Users/张三/.claude-work", root.GetProperty("account").GetString());
        Assert.Equal(1794069588307, root.GetProperty("ts").GetInt64());
        Assert.Equal(payload, root.GetProperty("payload").GetString());
    }

    [Fact]
    public void Account_is_empty_when_the_agent_uses_its_default_directory()
    {
        var environment = SignalEnvironment.From(new Dictionary<string, string> { [SignalEnvironment.AccountVariableName] = "CODEX_HOME" });
        Assert.Equal("", environment.Account);
    }
}

public sealed class SignalThrottleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "octopus-signal-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Fact]
    public void Minimum_interval_holds_back_any_payload()
    {
        var throttle = new SignalThrottle(_directory);
        Assert.True(throttle.ShouldSend("item", "statusline", "a", 5000, 10_000));
        Assert.False(throttle.ShouldSend("item", "statusline", "b", 5000, 14_999));
        Assert.True(throttle.ShouldSend("item", "statusline", "b", 5000, 15_000));
    }

    [Fact]
    public void The_same_payload_is_not_resent_within_the_repeat_window()
    {
        var throttle = new SignalThrottle(_directory);
        Assert.True(throttle.ShouldSend("item", "stop", "same", 0, 10_000));
        Assert.False(throttle.ShouldSend("item", "stop", "same", 0, 10_000 + SignalThrottle.RepeatWindowMs - 1));
        Assert.True(throttle.ShouldSend("item", "stop", "other", 0, 10_001));
        Assert.True(throttle.ShouldSend("item", "stop", "other", 0, 10_001 + SignalThrottle.RepeatWindowMs));
    }

    [Fact]
    public void Items_and_channels_are_throttled_separately()
    {
        var throttle = new SignalThrottle(_directory);
        Assert.True(throttle.ShouldSend("item-1", "statusline", "x", 60_000, 1000));
        Assert.True(throttle.ShouldSend("item-2", "statusline", "x", 60_000, 1001));
        Assert.True(throttle.ShouldSend("item-1", "stop", "x", 60_000, 1002));
    }
}

public class ChainCommandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"shell\":\"\"}")]
    [InlineData("{\"argv\":[]}")]
    [InlineData("{\"other\":1}")]
    public void Unusable_values_mean_no_chained_command(string? json) => Assert.Null(ChainCommand.Parse(json));

    [Fact]
    public void Parses_a_shell_command_or_an_argument_list()
    {
        Assert.Equal("~/.claude/statusline.sh", ChainCommand.Parse("{\"shell\":\"~/.claude/statusline.sh\"}")!.Shell);
        Assert.Equal(["/usr/bin/notify", "--quiet"], ChainCommand.Parse("{\"argv\":[\"/usr/bin/notify\",\"--quiet\"]}")!.Argv);
    }
}

/// <summary>The forwarder as an agent runs it, against a real Unix socket (macOS and Linux).</summary>
public sealed class ForwarderEndToEndTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("octosig").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private SignalThrottle Throttle => new(Path.Combine(_directory, "throttle"));

    private Dictionary<string, string> Environment(string endpoint, string? chain = null)
    {
        var env = new Dictionary<string, string>
        {
            [SignalEnvironment.EndpointVariable] = endpoint,
            [SignalEnvironment.TokenVariable] = "secret-token",
            [SignalEnvironment.ItemVariable] = "item-7",
            ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
        };
        if (chain is not null)
        {
            env[SignalEnvironment.ChainVariable] = chain;
        }
        return env;
    }

    /// <summary>A listener that accepts one connection and returns everything sent on it.</summary>
    private static (string Path, Task<string> Received) Listen(string directory)
    {
        var path = Path.Combine(directory, "s.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var received = Task.Run(async () =>
        {
            using (listener)
            using (var connection = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(10)))
            {
                var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int n;
                while ((n = await connection.ReceiveAsync(chunk)) > 0)
                {
                    buffer.Write(chunk, 0, n);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        });
        return (path, received);
    }

    private static MemoryStream Input(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Status_line_is_forwarded_and_the_users_own_status_line_still_prints()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket and /bin/sh");
        var (socket, received) = Listen(_directory);
        var payload = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":42,\"resets_at\":1794070000}}}";
        var stdout = new MemoryStream();

        var exit = await Forwarder.RunAsync(
            ["statusline", "--agent", "claude", "--channel", "statusline"],
            SignalEnvironment.From(Environment(socket, "{\"shell\":\"printf 'mine:'; cat\"}")),
            Input(payload), stdout, Throttle);

        Assert.Equal(0, exit);
        Assert.Equal("mine:" + payload, Encoding.UTF8.GetString(stdout.ToArray()));
        using var envelope = JsonDocument.Parse(await received);
        Assert.Equal(payload, envelope.RootElement.GetProperty("payload").GetString());
        Assert.Equal("item-7", envelope.RootElement.GetProperty("item").GetString());
    }

    [Fact]
    public async Task The_users_status_line_exit_code_is_passed_through()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/bin/sh");
        var exit = await Forwarder.RunAsync(
            ["statusline", "--agent", "claude", "--channel", "statusline"],
            SignalEnvironment.From(Environment(Path.Combine(_directory, "nobody.sock"), "{\"shell\":\"exit 3\"}")),
            Input("{}"), new MemoryStream(), Throttle);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task Chained_commands_do_not_see_the_signal_token()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/bin/sh");
        var stdout = new MemoryStream();
        await Forwarder.RunAsync(
            ["statusline", "--agent", "claude", "--channel", "statusline"],
            SignalEnvironment.From(Environment(Path.Combine(_directory, "nobody.sock"), "{\"shell\":\"printf '%s|%s' \\\"$OCTOPUS_SIGNAL_TOKEN\\\" \\\"$OCTOPUS_ITEM_ID\\\"\"}")),
            Input("{}"), stdout, Throttle);
        Assert.Equal("|item-7", Encoding.UTF8.GetString(stdout.ToArray()));
    }

    [Fact]
    public async Task Codex_notify_runs_the_users_own_program_with_the_payload_last()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/bin/sh");
        var (socket, received) = Listen(_directory);
        var output = Path.Combine(_directory, "notified.txt");
        var payload = "{\"type\":\"agent-turn-complete\",\"last-assistant-message\":\"it's done\"}";
        var chain = JsonSerializer.Serialize(new Dictionary<string, string[]> { ["argv"] = ["/bin/sh", "-c", $"printf %s \"$0\" > '{output}'"] });

        var exit = await Forwarder.RunAsync(
            ["notify", "--agent", "codex", "--channel", "turn-complete", payload],
            SignalEnvironment.From(Environment(socket, chain)),
            Input(""), new MemoryStream(), Throttle);

        Assert.Equal(0, exit);
        Assert.Equal(payload, await File.ReadAllTextAsync(output));
        using var envelope = JsonDocument.Parse(await received);
        Assert.Equal(payload, envelope.RootElement.GetProperty("payload").GetString());
    }

    [Fact]
    public async Task Nobody_listening_is_not_an_error_and_the_hook_reply_is_printed()
    {
        var stdout = new MemoryStream();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var exit = await Forwarder.RunAsync(
            ["hook", "--agent", "cursor", "--channel", "stop", "--reply", "{}"],
            SignalEnvironment.From(Environment(Path.Combine(_directory, "nobody.sock"))),
            Input("{\"hook_event_name\":\"stop\"}"), stdout, Throttle);

        Assert.Equal(0, exit);
        Assert.Equal("{}", Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"took {started.Elapsed}");
    }

    [Fact]
    public async Task Without_Octopus_in_the_environment_it_only_does_the_slots_job()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/bin/sh");
        var stdout = new MemoryStream();
        var env = new Dictionary<string, string> { [SignalEnvironment.ChainVariable] = "{\"shell\":\"cat\"}", ["PATH"] = "/usr/bin:/bin" };
        var exit = await Forwarder.RunAsync(["statusline", "--agent", "claude", "--channel", "statusline"],
            SignalEnvironment.From(env), Input("{\"model\":{}}"), stdout, Throttle);
        Assert.Equal(0, exit);
        Assert.Equal("{\"model\":{}}", Encoding.UTF8.GetString(stdout.ToArray()));
    }
}
