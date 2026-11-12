namespace Octopus.Ssh.Tests;

public class SshConfigParserTests
{
    /// <summary>A home folder with the given files under it (paths relative to home). Returns the home folder.</summary>
    private static string Home(params (string Path, string Text)[] files)
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-").FullName;
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(home, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return home;
    }

    private static IReadOnlyList<SshHost> Parse(string config)
    {
        var home = Home((Path.Combine(".ssh", "config"), config));
        return SshConfigParser.Parse(Path.Combine(home, ".ssh", "config"), home);
    }

    [Fact]
    public void Concrete_aliases_are_listed_in_file_order_and_patterns_are_not()
    {
        var hosts = Parse("""
            Host web
              HostName web.example.com
            Host *.internal !bad.internal
              User ops
            Host db
            Host web
            """);

        Assert.Equal(["web", "db"], hosts.Select(h => h.Alias));
    }

    [Fact]
    public void A_name_that_starts_with_a_dash_is_an_option_to_ssh_and_not_a_host()
    {
        var hosts = Parse("""
            Host -oProxyCommand=evil
            Host fine
            """);

        Assert.Equal(["fine"], hosts.Select(h => h.Alias));
    }

    [Fact]
    public void Each_option_takes_the_first_value_obtained_across_the_blocks_that_apply()
    {
        var hosts = Parse("""
            Host web
              HostName 10.0.0.5
              User alice
              Port 2222
            Host db
            Host *
              User bob
              Port 22
              IdentityFile ~/.ssh/id_ed25519
            """);

        var web = hosts.Single(h => h.Alias == "web");
        Assert.Equal("10.0.0.5", web.HostName);
        Assert.Equal("alice", web.User);
        Assert.Equal(2222, web.Port);
        Assert.Equal("~/.ssh/id_ed25519", web.IdentityFile);

        var db = hosts.Single(h => h.Alias == "db");
        Assert.Equal("db", db.HostName);
        Assert.Equal("bob", db.User);
        Assert.Equal(22, db.Port);
    }

    [Fact]
    public void Options_before_the_first_host_line_apply_to_every_host()
    {
        var hosts = Parse("""
            User everyone
            Host a
              HostName a.example
            """);

        Assert.Equal("everyone", hosts.Single().User);
    }

    [Fact]
    public void A_negated_pattern_keeps_its_host_out_of_the_block()
    {
        var hosts = Parse("""
            Host * !skip
              User default-user
            Host skip
            Host other
            """);

        Assert.Null(hosts.Single(h => h.Alias == "skip").User);
        Assert.Equal("default-user", hosts.Single(h => h.Alias == "other").User);
    }

    [Fact]
    public void Equals_signs_quotes_and_mixed_case_keywords_are_read()
    {
        var hosts = Parse("""
            Host  edge
                hostname=  edge.example
                IdentityFile "~/.ssh/with space"
                Port   2200
                ProxyJump = bastion
            # a comment line
            """);

        var edge = Assert.Single(hosts);
        Assert.Equal("edge.example", edge.HostName);
        Assert.Equal("~/.ssh/with space", edge.IdentityFile);
        Assert.Equal(2200, edge.Port);
        Assert.Equal("bastion", edge.ProxyJump);
    }

    [Fact]
    public void Include_is_followed_from_the_ssh_folder_and_its_options_land_in_the_open_block()
    {
        var home = Home(
            (Path.Combine(".ssh", "config"), """
                Host web
                  Include extra/*.conf
                Host db
                  HostName db.example
                """),
            (Path.Combine(".ssh", "extra", "a.conf"), """
                  User from-include
                Host inc
                  HostName inc.example
                """));

        var hosts = SshConfigParser.Parse(Path.Combine(home, ".ssh", "config"), home);

        Assert.Equal(["web", "inc", "db"], hosts.Select(h => h.Alias));
        Assert.Equal("from-include", hosts.Single(h => h.Alias == "web").User);
        Assert.Null(hosts.Single(h => h.Alias == "db").User);
        Assert.Equal("inc.example", hosts.Single(h => h.Alias == "inc").HostName);
    }

    [Fact]
    public void Match_blocks_are_skipped()
    {
        var hosts = Parse("""
            Host web
              HostName web.example
            Match host web
              User matched
            Host db
            """);

        Assert.Null(hosts.Single(h => h.Alias == "web").User);
        Assert.Null(hosts.Single(h => h.Alias == "db").User);
    }

    [Fact]
    public void An_include_that_includes_itself_stops_at_the_depth_limit()
    {
        var home = Home((Path.Combine(".ssh", "config"), "Include config\nHost h\n"));

        var hosts = SshConfigParser.Parse(Path.Combine(home, ".ssh", "config"), home);

        Assert.Equal(["h"], hosts.Select(h => h.Alias));
    }

    [Fact]
    public void A_missing_include_or_file_is_skipped()
    {
        var hosts = Parse("""
            Include does-not-exist.conf
            Include nothing/*.conf
            Host ok
            """);

        Assert.Equal(["ok"], hosts.Select(h => h.Alias));
    }

    [Fact]
    public void Port_must_be_a_number_in_range()
    {
        var hosts = Parse("""
            Host text
              Port abc
            Host big
              Port 70000
            Host fine
              Port 65535
            """);

        Assert.Null(hosts.Single(h => h.Alias == "text").Port);
        Assert.Null(hosts.Single(h => h.Alias == "big").Port);
        Assert.Equal(65535, hosts.Single(h => h.Alias == "fine").Port);
    }

    [Fact]
    public void Tilde_in_an_include_means_the_home_folder()
    {
        var home = Home(
            (Path.Combine(".ssh", "config"), "Include ~/shared/*\n"),
            (Path.Combine("shared", "team"), "Host from-home\n"));

        var hosts = SshConfigParser.Parse(Path.Combine(home, ".ssh", "config"), home);

        Assert.Equal(["from-home"], hosts.Select(h => h.Alias));
    }

    [Theory]
    [InlineData("*.example.com", "A.EXAMPLE.com", true)]
    [InlineData("web?", "web1", true)]
    [InlineData("web?", "web12", false)]
    [InlineData("a*b*c", "axxbyyc", true)]
    [InlineData("a*b*c", "axxbyy", false)]
    [InlineData("*", "anything", true)]
    [InlineData("db", "db2", false)]
    public void Patterns_follow_the_ssh_rules(string pattern, string host, bool expected) =>
        Assert.Equal(expected, SshConfigParser.Matches(pattern, host));
}
