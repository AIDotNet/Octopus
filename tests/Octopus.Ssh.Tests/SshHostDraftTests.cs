namespace Octopus.Ssh.Tests;

public class SshHostDraftTests
{
    private static SshHostProblem? ProblemOf(SshHostDraft draft, SshHostField field) =>
        draft.Validate([]).Where(i => i.Field == field).Select(i => (SshHostProblem?)i.Problem).FirstOrDefault();

    [Fact]
    public void A_draft_with_only_an_alias_is_valid()
    {
        Assert.Empty(new SshHostDraft("prod-web").Validate([]));
    }

    [Theory]
    [InlineData("", SshHostProblem.Required)]
    [InlineData("   ", SshHostProblem.Required)]
    [InlineData("bad alias", SshHostProblem.AliasFormat)]
    [InlineData("-oProxy", SshHostProblem.AliasFormat)]
    [InlineData("*", SshHostProblem.AliasFormat)]
    [InlineData("a:b", SshHostProblem.AliasFormat)]
    [InlineData("web.example-1_a", null)]
    [InlineData("137.184.127.219", null)]
    [InlineData("生产服务器", null)]
    [InlineData("测试-01", null)]
    [InlineData("跳板机.内网", null)]
    [InlineData("𠮷野家", null)]
    [InlineData("服务器 一", SshHostProblem.AliasFormat)]
    [InlineData("服务器：一", SshHostProblem.AliasFormat)]
    [InlineData("-服务器", SshHostProblem.AliasFormat)]
    public void The_alias_is_required_and_has_a_format(string alias, SshHostProblem? expected) =>
        Assert.Equal(expected, ProblemOf(new SshHostDraft(alias), SshHostField.Alias));

    [Fact]
    public void An_alias_that_is_already_a_host_is_refused_in_any_case()
    {
        var issues = new SshHostDraft("web").Validate(["Web"]);

        Assert.Equal([new SshHostIssue(SshHostField.Alias, SshHostProblem.AliasExists)], issues);
    }

    [Fact]
    public void Values_are_single_words_except_the_key_path()
    {
        Assert.Equal(SshHostProblem.Word, ProblemOf(new SshHostDraft("a", HostName: "two words"), SshHostField.HostName));
        Assert.Equal(SshHostProblem.Word, ProblemOf(new SshHostDraft("a", User: "x\"y"), SshHostField.User));
        Assert.Equal(SshHostProblem.Word, ProblemOf(new SshHostDraft("a", ProxyJump: "-oProxy"), SshHostField.ProxyJump));
        Assert.Equal(SshHostProblem.Word, ProblemOf(new SshHostDraft("a", User: "x#y"), SshHostField.User));
        Assert.Null(ProblemOf(new SshHostDraft("a", HostName: "10.0.0.5", User: "root", ProxyJump: "bastion,jump@x:2200"), SshHostField.HostName));
        Assert.Null(ProblemOf(new SshHostDraft("a", IdentityFile: "~/.ssh/my key"), SshHostField.IdentityFile));
        Assert.Equal(SshHostProblem.KeyPath, ProblemOf(new SshHostDraft("a", IdentityFile: "~/.ssh/\"x\""), SshHostField.IdentityFile));
    }

    [Theory]
    [InlineData("22", null)]
    [InlineData("65535", null)]
    [InlineData("0", SshHostProblem.Port)]
    [InlineData("65536", SshHostProblem.Port)]
    [InlineData("abc", SshHostProblem.Port)]
    [InlineData("-1", SshHostProblem.Port)]
    public void The_port_is_a_number_from_1_to_65535(string port, SshHostProblem? expected) =>
        Assert.Equal(expected, ProblemOf(new SshHostDraft("a", Port: port), SshHostField.Port));

    [Fact]
    public void A_telnet_host_does_not_check_the_fields_it_does_not_use()
    {
        var draft = new SshHostDraft("路由器", User: "x y", IdentityFile: "a\"b", ProxyJump: "-o", Protocol: SshHostProtocol.Telnet);

        Assert.Empty(draft.Validate([]));
        Assert.Equal(SshHostProblem.Port, ProblemOf(draft with { Port = "70000" }, SshHostField.Port));
    }

    [Fact]
    public void A_host_gives_back_the_draft_it_was_made_from()
    {
        var host = new SshHost("盒子", "10.0.0.5", "root", 2222, "~/.ssh/k", "bastion", SshHostProtocol.Mosh);

        Assert.Equal(new SshHostDraft("盒子", "10.0.0.5", "root", "2222", "~/.ssh/k", "bastion", SshHostProtocol.Mosh), host.ToDraft());
        Assert.Null(new SshHost("web", "web", null, null, null, null).ToDraft().HostName);
    }
}
