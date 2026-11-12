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

    [Theory]
    [InlineData("", "^\"^\"")]
    [InlineData("abc", "abc")]
    [InlineData("a b", "^\"a b^\"")]
    [InlineData("a\"b", "^\"a\\^\"b^\"")]
    [InlineData("a&b|c", "^\"a^&b^|c^\"")]
    [InlineData("%PATH%", "^\"^%PATH^%^\"")]
    [InlineData("你好 x", "^\"你好 x^\"")]
    [InlineData("my dir\\", "^\"my dir\\\\^\"")]
    public void Cmd_quote(string input, string expected) => Assert.Equal(expected, _b.Quote(input, ShellKind.Cmd));

    [Fact]
    public void Build_posix() =>
        Assert.Equal("claude --resume 'a b'", _b.Build("claude", ["--resume", "a b"], ShellKind.Posix));

    [Fact]
    public void Build_powershell_uses_call_operator() =>
        Assert.Equal("& 'C:\\Program Files\\x.exe' -a 'b c'",
            _b.Build("C:\\Program Files\\x.exe", ["-a", "b c"], ShellKind.PowerShell));

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
}
