namespace Octopus.Ssh.Tests;

public sealed class SshAliasTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "octopus-alias-" + Guid.NewGuid().ToString("N"));

    public SshAliasTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".ssh"));
        File.WriteAllText(Path.Combine(_home, ".ssh", "config"),
            "Host Master\n    HostName 1.2.3.4\n\nHost 香港主服务器\n    HostName 43.134.216.115\n    User root\n\nHost *\n    ServerAliveInterval 30\n");
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Theory]
    [InlineData("Master", true)]
    [InlineData("137.184.127.219", true)]
    [InlineData("user@host.example", true)]
    [InlineData("香港主服务器", false)]
    [InlineData("host name", false)]
    public void Plain_names_are_the_ones_ssh_takes(string host, bool plain) => Assert.Equal(plain, SshAlias.IsPlain(host));

    [Fact]
    public void A_plain_host_is_left_alone()
    {
        string[] arguments = ["-t", "--", "Master", "exit 0"];
        Assert.Same(arguments, SshAlias.Prepare(arguments, _home));
    }

    [Fact]
    public void An_alias_ssh_does_not_take_gets_an_ascii_name_and_a_generated_file()
    {
        var result = SshAlias.Prepare(["-o", "BatchMode=yes", "--", "香港主服务器", "exit 0"], _home);

        var name = SshAlias.WireName("香港主服务器");
        Assert.Equal("-F", result[0]);
        Assert.Equal(["-o", "BatchMode=yes", "--", name, "exit 0"], result.Skip(2));
        var file = File.ReadAllText(result[1]);
        Assert.StartsWith("# Generated", file);
        Assert.Contains("Host " + name, file);
        Assert.Contains("HostName 43.134.216.115", file);
        Assert.Contains("User root", file);
        Assert.DoesNotContain("ServerAliveInterval", file);
        Assert.Contains("Match all", file);
        Assert.Contains("Include \"" + Path.Combine(_home, ".ssh", "config").Replace('\\', '/') + "\"", file);
        Assert.All(name, c => Assert.True(c < 128));
    }

    [Fact]
    public void An_alias_without_a_block_is_left_alone()
    {
        string[] arguments = ["--", "没有这台", "exit 0"];
        Assert.Same(arguments, SshAlias.Prepare(arguments, _home));
    }
}
