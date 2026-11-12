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
}
