using Octopus.Platform.Linux;
using Octopus.Platform.MacOS;
using Octopus.Platform.Unix;

namespace Octopus.Platform.Tests;

public class UnixTerminalEnvironmentTests
{
    private sealed class Env(Dictionary<string, string> snapshot, string lang = "fallback.UTF-8")
        : UnixTerminalEnvironmentBase(snapshot, "9.9")
    {
        protected override string GetDefaultLang() => lang;
    }

    [Fact]
    public void Strips_host_variables_and_keeps_others()
    {
        var env = new Env(new()
        {
            ["PATH"] = "/bin", ["HOME"] = "/h", ["LANG"] = "en_US.UTF-8",
            ["OCTOPUS_TOKEN"] = "x", ["TKCODE_X"] = "x", ["TMUX"] = "x", ["TMUX_PANE"] = "x", ["TERMCAP"] = "x",
            ["SHLVL"] = "2", ["__CFBundleIdentifier"] = "x", ["XPC_FLAGS"] = "x", ["ITERM_SESSION_ID"] = "x",
            ["VSCODE_PID"] = "1", ["DOTNET_STARTUP_HOOKS"] = "x", ["COR_ENABLE_PROFILING"] = "1", ["WT_SESSION"] = "x",
        }).Build();
        Assert.Equal(new[] { "HOME", "LANG", "PATH", "COLORTERM", "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION" }.Order(),
            env.Keys.Order());
        Assert.Equal("en_US.UTF-8", env["LANG"]);
    }

    [Fact]
    public void Sets_terminal_variables()
    {
        var env = new Env(new() { ["TERM"] = "dumb", ["TERM_PROGRAM"] = "Apple_Terminal" }).Build();
        Assert.Equal("xterm-256color", env["TERM"]);
        Assert.Equal("truecolor", env["COLORTERM"]);
        Assert.Equal("Octopus", env["TERM_PROGRAM"]);
        Assert.Equal("9.9", env["TERM_PROGRAM_VERSION"]);
    }

    [Fact]
    public void Fills_missing_lang_and_extra_wins()
    {
        var env = new Env(new() { ["PATH"] = "/bin" }).Build(new Dictionary<string, string>
        {
            ["TERM"] = "screen", ["FOO"] = "1",
        });
        Assert.Equal("fallback.UTF-8", env["LANG"]);
        Assert.Equal("screen", env["TERM"]);
        Assert.Equal("1", env["FOO"]);
    }

    [Fact]
    public void Linux_keeps_non_utf8_lang_but_fills_missing()
    {
        Assert.Equal("C", new LinuxTerminalEnvironment(new Dictionary<string, string> { ["LANG"] = "C" }).Build()["LANG"]);
        Assert.Equal("C.UTF-8", new LinuxTerminalEnvironment(new Dictionary<string, string>()).Build()["LANG"]);
    }

    [Fact]
    public void Mac_apple_locale_mapping()
    {
        Assert.Equal("zh_CN.UTF-8", MacTerminalEnvironment.LangFromAppleLocale("zh_CN\n", _ => true));
        Assert.Equal("en_US.UTF-8", MacTerminalEnvironment.LangFromAppleLocale("xx_YY", _ => false));
        Assert.Equal("en_GB.UTF-8", MacTerminalEnvironment.LangFromAppleLocale("en_GB@rg=cnzzzz", n => n == "en_GB.UTF-8"));
    }

    [Fact]
    public void Parsers()
    {
        Assert.Equal("/bin/zsh", MacShellResolver.ParseDsclOutput("UserShell: /bin/zsh\n"));
        Assert.Null(MacShellResolver.ParseDsclOutput("nothing"));
        Assert.Equal("/usr/bin/fish", LinuxShellResolver.ParsePasswdLine("u:x:1000:1000:U:/home/u:/usr/bin/fish\n"));
        var parsed = UnixLoginEnvironment.Parse("banner\nMK" + "A=1\0B=x=y\0", "MK");
        Assert.Equal("1", parsed!["A"]);
        Assert.Equal("x=y", parsed["B"]);
        Assert.Null(UnixLoginEnvironment.Parse("A=1\0", "MK"));
    }
}
