namespace Octopus.Ssh.Tests;

public class SshConfigWriterTests
{
    private static readonly SshHostDraft Box = new("box", HostName: "10.0.0.1", User: "root");

    [Fact]
    public void The_block_holds_only_the_fields_that_were_given()
    {
        Assert.Equal("Host box\n    HostName 10.0.0.1\n    User root\n", SshConfigWriter.Insert("", Box));
    }

    [Fact]
    public void A_value_with_a_space_is_quoted()
    {
        var text = SshConfigWriter.Insert("", new SshHostDraft("box", IdentityFile: "~/.ssh/my key"));

        Assert.Equal("Host box\n    IdentityFile \"~/.ssh/my key\"\n", text);
    }

    [Fact]
    public void Without_a_generic_block_the_new_host_goes_at_the_end_after_a_blank_line()
    {
        var text = SshConfigWriter.Insert("Host a\n    HostName a.example\n", Box);

        Assert.Equal("Host a\n    HostName a.example\n\nHost box\n    HostName 10.0.0.1\n    User root\n", text);
    }

    [Fact]
    public void The_new_host_goes_before_a_wildcard_block_so_that_its_own_options_win()
    {
        var text = SshConfigWriter.Insert("Host a\n  HostName a\n\nHost *\n  User bob\n", Box);

        Assert.Equal("Host a\n  HostName a\n\nHost box\n    HostName 10.0.0.1\n    User root\n\nHost *\n  User bob\n", text);
    }

    [Theory]
    [InlineData("Host !skip\n  User x\n")]
    [InlineData("Host *.internal\n  User x\n")]
    [InlineData("Match all\n  User x\n")]
    public void Negated_patterns_and_match_blocks_count_as_generic(string generic)
    {
        var text = SshConfigWriter.Insert(generic, Box);

        Assert.StartsWith("Host box\n", text);
        Assert.EndsWith(generic, text);
    }

    [Fact]
    public void Line_endings_of_the_file_are_kept()
    {
        var text = SshConfigWriter.Insert("Host *\r\n  User bob\r\n", Box);

        Assert.Equal("Host box\r\n    HostName 10.0.0.1\r\n    User root\r\n\r\nHost *\r\n  User bob\r\n", text);
    }

    [Fact]
    public void A_file_without_a_final_line_break_gets_one_before_the_block()
    {
        var text = SshConfigWriter.Insert("Host a", new SshHostDraft("box"));

        Assert.Equal("Host a\n\nHost box\n", text);
    }

    [Fact]
    public void A_chinese_alias_is_written_as_it_is_and_read_back()
    {
        var text = SshConfigWriter.Insert("", new SshHostDraft("生产服务器", HostName: "10.0.0.1"));
        var home = Directory.CreateTempSubdirectory("octopus-ssh-").FullName;
        var path = Path.Combine(home, "config");
        File.WriteAllText(path, text);

        Assert.Equal("Host 生产服务器\n    HostName 10.0.0.1\n", text);
        Assert.Equal(["生产服务器"], SshConfigParser.Parse(path, home).Select(h => h.Alias));
    }

    [Fact]
    public void The_protocol_is_kept_in_a_comment_that_ssh_ignores_and_is_read_back()
    {
        var text = SshConfigWriter.Insert("", new SshHostDraft("box", "10.0.0.1", User: "root", Protocol: SshHostProtocol.Mosh));
        var home = Directory.CreateTempSubdirectory("octopus-ssh-").FullName;
        var path = Path.Combine(home, "config");
        File.WriteAllText(path, text);

        Assert.Equal("Host box\n    # octopus: protocol=mosh\n    HostName 10.0.0.1\n    User root\n", text);
        Assert.Equal(SshHostProtocol.Mosh, SshConfigParser.Parse(path, home).Single().Protocol);
    }

    [Fact]
    public void A_telnet_block_has_only_the_host_name_and_the_port()
    {
        var text = SshConfigWriter.Insert("", new SshHostDraft("路由器", "192.168.1.1", User: "admin", Port: "23", IdentityFile: "k", ProxyJump: "j", Protocol: SshHostProtocol.Telnet));

        Assert.Equal("Host 路由器\n    # octopus: protocol=telnet\n    HostName 192.168.1.1\n    Port 23\n", text);
    }

    private const string Two = "# my hosts\nHost web\n    HostName web.example\n    User ops\n    ForwardAgent yes\n    # keep this\n\nHost db\n    HostName db.example\n\nHost *\n    User bob\n";

    [Fact]
    public void An_edit_changes_adds_and_removes_the_managed_options_and_keeps_every_other_line()
    {
        var text = SshConfigWriter.Update(Two, "web", new SshHostDraft("web", "web2.example", User: null, Port: "2222"));

        Assert.Equal(
            "# my hosts\nHost web\n    HostName web2.example\n    ForwardAgent yes\n    Port 2222\n    # keep this\n\nHost db\n    HostName db.example\n\nHost *\n    User bob\n",
            text);
    }

    [Fact]
    public void An_edit_can_rename_and_change_the_protocol_and_back()
    {
        var mosh = SshConfigWriter.Update(Two, "db", new SshHostDraft("数据库", "db.example", Protocol: SshHostProtocol.Mosh))!;
        Assert.Contains("Host 数据库\n    # octopus: protocol=mosh\n    HostName db.example\n", mosh);

        var ssh = SshConfigWriter.Update(mosh, "数据库", new SshHostDraft("数据库", "db.example"))!;
        Assert.Contains("Host 数据库\n    HostName db.example\n", ssh);
        Assert.DoesNotContain("octopus:", ssh);
    }

    [Fact]
    public void An_edit_to_telnet_leaves_the_options_telnet_does_not_use()
    {
        var text = SshConfigWriter.Update(Two, "web", new SshHostDraft("web", "web.example", Protocol: SshHostProtocol.Telnet))!;

        Assert.Contains("    User ops\n", text);
        Assert.Contains("    ForwardAgent yes\n", text);
        Assert.Contains("    # octopus: protocol=telnet\n", text);
    }

    [Fact]
    public void An_edit_keeps_the_line_endings_and_the_indent_of_the_file()
    {
        var text = SshConfigWriter.Update("Host a\r\n\tHostName a.example\r\n\tUser x\r\n", "a", new SshHostDraft("a", "b.example", User: "x", Port: "22"))!;

        Assert.Equal("Host a\r\n\tHostName b.example\r\n\tUser x\r\n\tPort 22\r\n", text);
    }

    [Fact]
    public void Removing_a_host_takes_its_block_and_leaves_the_rest()
    {
        Assert.Equal(
            "# my hosts\n    # keep this\n\nHost db\n    HostName db.example\n\nHost *\n    User bob\n",
            SshConfigWriter.Remove(Two, "web"));
        Assert.Equal(
            "# my hosts\nHost web\n    HostName web.example\n    User ops\n    ForwardAgent yes\n    # keep this\n\nHost *\n    User bob\n",
            SshConfigWriter.Remove(Two, "DB"));
    }

    [Fact]
    public void Removing_the_only_host_leaves_an_empty_file()
    {
        Assert.Equal("", SshConfigWriter.Remove("Host a\n    User x\n", "a"));
    }

    [Theory]
    [InlineData("Host a b\n  User x\n")]
    [InlineData("Host *\n  User x\n")]
    [InlineData("Match all\n  User x\n")]
    [InlineData("")]
    public void A_host_without_a_block_of_its_own_cannot_be_changed_or_removed(string config)
    {
        Assert.False(SshConfigWriter.HasBlock(config, "a"));
        Assert.Null(SshConfigWriter.Update(config, "a", new SshHostDraft("a")));
        Assert.Null(SshConfigWriter.Remove(config, "a"));
    }
}
