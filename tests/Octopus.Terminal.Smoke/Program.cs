using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Octopus.Core.Terminals;
using Octopus.Terminal.Avalonia;
using Octopus.Terminal.MiniPty;

// Manual check of TerminalControl. Without arguments: a window with a login shell.
// With --script: drives the terminal and exits 0 (SMOKE OK) or 1.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<SmokeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

internal sealed class SmokeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var args = Environment.GetCommandLineArgs();
        var script = args.Contains("--script");
        var terminal = new TerminalControl();
        var window = new Window { Width = 900, Height = 560, Title = "Octopus terminal smoke", Content = terminal };
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).MainWindow = window;
        terminal.TitleChanged += (_, t) => window.Title = t;

        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s ? s : "/bin/zsh";
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "Octopus";
        var spec = new LaunchSpec(shell, ["-l"], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), env);
        terminal.Start(spec, new MiniPtyConnectionFactory());
        window.Opened += (_, _) => terminal.FocusTerminal();

        if (script) window.Opened += async (_, _) => Environment.Exit(await ScriptRunner.RunAsync(terminal, window));
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class ScriptRunner
{
    public static async Task<int> RunAsync(TerminalControl t, Window window)
    {
        try
        {
            if (!await WaitFor(t, s => s.Trim().Length > 0, 15)) return Fail(t, "shell never printed anything");
            await Task.Delay(1500); // let the profile finish

            // 1. a command runs and its output reaches the screen
            t.SendText("echo OCTOPUS_SMOKE_$((40+2))\r");
            await Task.Delay(3000);
            if (!t.ReadScreenText().Contains("OCTOPUS_SMOKE_42")) return Fail(t, "echo output missing");
            Console.WriteLine("step 1 echo: ok");

            // 2. resize reaches the process
            window.Width = 700;
            window.Height = 420;
            await Task.Delay(1200);
            var grid = t.GridSize ?? (0, 0);
            t.SendText("stty size\r");
            if (!await WaitFor(t, s => Regex.IsMatch(s, $@"(?m)^{grid.Item2} {grid.Item1}\s*$"), 5))
                return Fail(t, $"stty size does not match grid {grid.Item2} {grid.Item1}");
            Console.WriteLine($"step 2 resize: ok (grid {grid.Item1}x{grid.Item2}, stty agrees)");

            // 3. device attributes reply is written back to the pty
            t.SendText("bash -c 'stty -echo raw; printf \"\\033[c\"; IFS= read -rd c -t 3 r; stty sane; printf \"REPLY_LEN_%d\\n\" ${#r}'\r");
            if (!await WaitFor(t, s => Regex.IsMatch(s, @"REPLY_LEN_\d+"), 8)) return Fail(t, "DA test did not finish (reply not written back?)");
            var len = int.Parse(Regex.Match(t.ReadScreenText(), @"REPLY_LEN_(\d+)").Groups[1].Value);
            if (len < 4) return Fail(t, $"DA reply too short: {len}");
            Console.WriteLine($"step 3 DA reply: ok ({len} bytes)");

            // 4. full-screen program: vim in and out
            t.SendText("vim -u NONE -N\r");
            await Task.Delay(2000);
            var vimScreen = t.ReadScreenText();
            var tildes = vimScreen.Split('\n').Count(l => l.Trim() == "~");
            if (tildes < 3) return Fail(t, "vim screen not shown");
            t.SendText(":q\r");
            await Task.Delay(800);
            t.SendText("echo OCTOPUS_AFTER_VIM\r");
            if (!await WaitFor(t, s => Regex.IsMatch(s, @"(?m)^OCTOPUS_AFTER_VIM\s*$"), 5)) return Fail(t, "no shell prompt after vim");
            Console.WriteLine($"step 4 vim: ok ({tildes} tilde rows shown, back at shell)");

            // 5. terminfo / colours
            t.SendText("tput colors\r");
            if (!await WaitFor(t, s => Regex.IsMatch(s, @"(?m)^256\s*$"), 5)) return Fail(t, "tput colors != 256");
            Console.WriteLine("step 5 tput colors: ok");

            // 6. exit: the process ends and is reported
            var exited = new TaskCompletionSource<int?>();
            t.Exited += (_, c) => exited.TrySetResult(c);
            t.SendText("exit 7\r");
            var code = await Task.WhenAny(exited.Task, Task.Delay(5000)) == exited.Task ? await exited.Task : -1;
            if (code != 7 || t.IsRunning) return Fail(t, $"exit not reported (code {code})");
            Console.WriteLine("step 6 exit: ok (code 7)");

            // 7. restart
            await t.RestartAsync();
            if (!await WaitFor(t, s => s.Trim().Length > 0, 10) || !t.IsRunning) return Fail(t, "restart failed");
            Console.WriteLine("step 7 restart: ok");

            await t.CloseAsync();
            Console.WriteLine("SMOKE OK");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(t, ex.ToString());
        }
    }

    private static async Task<bool> WaitFor(TerminalControl t, Func<string, bool> predicate, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(t.ReadScreenText())) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static int Fail(TerminalControl t, string why)
    {
        Console.WriteLine($"SMOKE FAILED: {why}");
        Console.WriteLine("---- screen ----");
        Console.WriteLine(t.ReadScreenText());
        return 1;
    }
}
