using Octopus.Platform;

namespace Octopus.Platform.Tests;

public class ShellCommandBuilderTests
{
    private readonly ShellCommandBuilder _b = new();

    [Theory]
    [InlineData("", "''")]
    [InlineData("abc", "abc")]
    [InlineData("/usr/bin/x-y_z.1", "/usr/bin/x-y_z.1")]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("`id`", "'`id`'")]
    [InlineData("a\nb", "'a\nb'")]
    [InlineData("你好", "'你好'")]
    [InlineData("a;b&c|d", "'a;b&c|d'")]
    public void Posix_quote(string input, string expected) => Assert.Equal(expected, _b.Quote(input, ShellKind.Posix));

    [Theory]
    [InlineData("", "''")]
    [InlineData("abc", "abc")]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", "'it\\'s'")]
    [InlineData("a\\b", "'a\\\\b'")]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("你好", "'你好'")]
    [InlineData("a;b&c|d", "'a;b&c|d'")]
    [InlineData("a\nb", "'a\nb'")]
    public void Fish_quote(string input, string expected) => Assert.Equal(expected, _b.Quote(input, ShellKind.Fish));

    [Theory]
    [InlineData("", "''")]
    [InlineData("abc", "abc")]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("1,2", "'1,2'")]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("`n", "'`n'")]
    [InlineData("你好", "'你好'")]
    [InlineData("a;b&c|d", "'a;b&c|d'")]
    [InlineData("a\nb", "'a\nb'")]
    public void PowerShell_quote(string input, string expected) => Assert.Equal(expected, _b.Quote(input, ShellKind.PowerShell));

    [Fact]
    public void Build_posix() =>
        Assert.Equal("claude --resume 'a b'", _b.Build("claude", ["--resume", "a b"], ShellKind.Posix));

    [Fact]
    public void Build_powershell_uses_call_operator() =>
        Assert.Equal("& 'C:\\Program Files\\x.exe' -a 'b c'",
            _b.Build("C:\\Program Files\\x.exe", ["-a", "b c"], ShellKind.PowerShell));

    [Fact]
    public void Build_powershell_escapes_double_quotes_for_native_programs() =>
        // Codex's notify: Windows PowerShell would drop the quotes and Codex would read a string, not an array (#18).
        Assert.Equal(
            "$PSNativeCommandArgumentPassing = 'Legacy'; & codex -c 'notify=[\\\"E:\\\\a b\\\\x.exe\\\",\\\"notify\\\"]' '\\\\\\\"' ''",
            _b.Build("codex", ["-c", "notify=[\"E:\\\\a b\\\\x.exe\",\"notify\"]", "\\\"", ""], ShellKind.PowerShell));

    [Fact]
    public void Build_powershell_without_double_quotes_is_unchanged() =>
        Assert.Equal("& codex -c 'model=gpt'", _b.Build("codex", ["-c", "model=gpt"], ShellKind.PowerShell));

    [Fact]
    public void WrapEnvironment_powershell_quotes_plain_words()
    {
        // A bare word after '=' is run as a command (#18): "810b… is not recognized as a cmdlet".
        var sets = new Dictionary<string, string>
        {
            ["OCTOPUS_SIGNAL_TOKEN"] = "810b4320",
            ["OCTOPUS_SIGNAL_ACCOUNT_VAR"] = "CODEX_HOME",
        };
        Assert.Equal(
            "$env:OCTOPUS_SIGNAL_TOKEN = '810b4320'; $env:OCTOPUS_SIGNAL_ACCOUNT_VAR = 'CODEX_HOME'; & codex",
            _b.WrapEnvironment("& codex", ShellKind.PowerShell, [], sets));
    }

    [Fact]
    public void Build_without_arguments() => Assert.Equal("ls", _b.Build("ls", [], ShellKind.Fish));

    [Theory]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("rec --profile work", new[] { "rec", "--profile", "work" })]
    [InlineData("'a b' \"c d\" e\\ f", new[] { "a b", "c d", "e f" })]
    [InlineData("\"a \\\"q\\\" b\"", new[] { "a \"q\" b" })]
    [InlineData("'' x", new[] { "", "x" })]
    [InlineData("   ", new string[0])]
    [InlineData("a'b c'd", new[] { "ab cd" })]
    public void Split_cases(string input, string[] expected) => Assert.Equal(expected, _b.Split(input));

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("it's")]
    [InlineData("say \"hi\" $HOME `x` \\ ")]
    [InlineData("你好 世界")]
    [InlineData("a;b&c|d")]
    [InlineData("a\nb")]
    public void Posix_and_fish_round_trip_through_split(string argument)
    {
        // Fish uses \' and \\ inside single quotes; Split only knows POSIX, so round-trip Posix only.
        var line = _b.Build("prog", [argument, "tail"], ShellKind.Posix);
        Assert.Equal(["prog", argument, "tail"], _b.Split(line));
    }

    [Fact]
    public void WrapEnvironment_handles_posix_fish_powershell_and_cmd()
    {
        var unsets = new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "BAD;NAME" };
        var sets = new Dictionary<string, string>
        {
            ["CLAUDE_CONFIG_DIR"] = "/Users/me/Library/App Support/Octopus/accounts/claude/a1",
            ["INVALID-KEY"] = "ignored",
        };

        Assert.Equal(
            "unset ANTHROPIC_API_KEY ANTHROPIC_AUTH_TOKEN; export CLAUDE_CONFIG_DIR='/Users/me/Library/App Support/Octopus/accounts/claude/a1'; claude",
            _b.WrapEnvironment("claude", ShellKind.Posix, unsets, sets));

        Assert.Equal(
            "set -e ANTHROPIC_API_KEY; set -e ANTHROPIC_AUTH_TOKEN; set -gx CLAUDE_CONFIG_DIR '/Users/me/Library/App Support/Octopus/accounts/claude/a1'; claude",
            _b.WrapEnvironment("claude", ShellKind.Fish, unsets, sets));

        Assert.Equal(
            "Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue; Remove-Item Env:ANTHROPIC_AUTH_TOKEN -ErrorAction SilentlyContinue; $env:CLAUDE_CONFIG_DIR = '/Users/me/Library/App Support/Octopus/accounts/claude/a1'; & claude",
            _b.WrapEnvironment("& claude", ShellKind.PowerShell, unsets, sets));

        Assert.Equal(
            "set \"ANTHROPIC_API_KEY=\" & set \"ANTHROPIC_AUTH_TOKEN=\" & set \"CLAUDE_CONFIG_DIR=/Users/me/Library/App Support/Octopus/accounts/claude/a1\" & claude",
            _b.WrapEnvironment("claude", ShellKind.Cmd, unsets, sets));

        Assert.Equal("claude", _b.WrapEnvironment("claude", ShellKind.Posix, [], new Dictionary<string, string>()));
    }

    [Fact]
    public void WrapWorkingDirectory_handles_posix_fish_powershell_and_cmd()
    {
        Assert.Equal(
            "cd -- '/Users/me/my project'; claude",
            _b.WrapWorkingDirectory("claude", ShellKind.Posix, "/Users/me/my project"));

        Assert.Equal(
            "cd -- 'D:\\code\\my project'; claude",
            _b.WrapWorkingDirectory("claude", ShellKind.Posix, @"D:\code\my project"));

        Assert.Equal(
            "cd -- '/Users/me/my project'; claude",
            _b.WrapWorkingDirectory("claude", ShellKind.Fish, "/Users/me/my project"));

        Assert.Equal(
            "Set-Location -LiteralPath 'D:\\code\\my [app]''s'; & claude",
            _b.WrapWorkingDirectory("& claude", ShellKind.PowerShell, @"D:\code\my [app]'s"));

        Assert.Equal(
            "cd /d \"D:\\code\\my project\" & claude",
            _b.WrapWorkingDirectory("claude", ShellKind.Cmd, @"D:\code\my project"));

        Assert.Equal(
            "cd /d \"D:\\\" & claude",
            _b.WrapWorkingDirectory("claude", ShellKind.Cmd, @"D:\"));

        Assert.Equal("claude", _b.WrapWorkingDirectory("claude", ShellKind.PowerShell, null));
        Assert.Equal("claude", _b.WrapWorkingDirectory("claude", ShellKind.PowerShell, "   "));
    }
}
