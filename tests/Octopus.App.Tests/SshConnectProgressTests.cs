using Avalonia.Headless.XUnit;
using Octopus.App.Terminals;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>The connection card of a terminal on an SSH host: it follows the log ssh writes, and says what failed.</summary>
public class SshConnectProgressTests
{
    private static string NewLog()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("octopus-connect-").FullName, "connect.log");
        File.WriteAllText(path, "");
        return path;
    }

    [AvaloniaFact]
    public void The_steps_follow_the_log_and_the_card_goes_away_when_the_session_opens()
    {
        var log = NewLog();
        using var progress = new SshConnectProgress("prod", log);
        progress.Begin();

        Assert.True(progress.IsVisible);
        Assert.True(progress.Steps[0].IsCurrent);

        File.AppendAllText(log, "debug1: Connecting to 10.0.0.5 [10.0.0.5] port 22.\ndebug1: Connection established.\n");
        Assert.True(Frames.Until(() => progress.Steps[1].IsCurrent && progress.Steps[0].IsDone));

        File.AppendAllText(log, "debug1: Server host key: ssh-ed25519 SHA256:abc\nWarning: Permanently added '10.0.0.5' (ED25519) to the list of known hosts.\n");
        Assert.True(Frames.Until(() => progress.Steps[2].IsCurrent && progress.Steps[1].IsDone));

        File.AppendAllText(log, "debug1: Authenticating to 10.0.0.5:22 as 'root'\n");
        Assert.True(Frames.Until(() => progress.Steps[3].IsCurrent && progress.Steps[2].IsDone));
        Assert.Contains("Authenticating to 10.0.0.5:22 as 'root'", progress.RecentLines);

        File.AppendAllText(log, "debug1: Entering interactive session.\n");
        Assert.True(Frames.Until(() => !progress.IsVisible));
        Assert.All(progress.Steps, step => Assert.True(step.IsDone));
    }

    [AvaloniaFact]
    public void A_failed_connection_stays_on_the_card_with_the_reason()
    {
        var log = NewLog();
        using var progress = new SshConnectProgress("prod", log);
        progress.Begin();

        File.AppendAllText(log, "debug1: Connecting to 10.0.0.5 [10.0.0.5] port 22.\nPermission denied (publickey,password).\n");
        Assert.True(Frames.Until(() => progress.Failure is not null));
        progress.Ended(255);

        Assert.Equal("Permission denied (publickey,password).", progress.Failure);
        Assert.Equal(progress.Failure, progress.Title);
        Assert.True(progress.IsVisible);
        Assert.False(progress.IsConnecting);
    }

    [AvaloniaFact]
    public void A_run_that_ends_before_it_is_ready_says_so_and_a_restart_reads_the_new_lines()
    {
        var log = NewLog();
        using var progress = new SshConnectProgress("prod", log);
        progress.Begin();
        File.AppendAllText(log, "debug1: Connection established.\n");
        Assert.True(Frames.Until(() => progress.Steps[1].IsCurrent));

        progress.Ended(255);
        Assert.Contains("255", progress.Failure);

        // A restart of the terminal empties the log and starts it again: the card follows the new run.
        File.WriteAllText(log, "");
        progress.Begin();
        Assert.Null(progress.Failure);
        Assert.True(progress.Steps[0].IsCurrent);
        File.AppendAllText(log, "debug1: Connecting to 10.0.0.5 [10.0.0.5] port 22.\n");
        Assert.True(Frames.Until(() => progress.Steps[0].IsCurrent && progress.RecentLines.Count == 1 && progress.RecentLines[0].StartsWith("Connecting to")));
    }
}
