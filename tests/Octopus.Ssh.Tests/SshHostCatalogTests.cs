namespace Octopus.Ssh.Tests;

public class SshHostCatalogTests
{
    [Fact]
    public void Without_a_config_file_there_are_no_hosts_and_the_scan_says_so()
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-").FullName;

        var catalog = new SshHostCatalog(home);

        Assert.False(catalog.Current.ConfigExists);
        Assert.Empty(catalog.Current.Hosts);
        Assert.Equal(Path.Combine(home, ".ssh", "config"), catalog.Current.ConfigPath);
    }

    [Fact]
    public void Refresh_reads_the_file_again_and_says_so()
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-").FullName;
        var config = Path.Combine(home, ".ssh", "config");
        var catalog = new SshHostCatalog(home);
        var raised = 0;
        catalog.Changed += (_, _) => raised++;

        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "Host first\n");
        catalog.Refresh();
        Assert.Equal(["first"], catalog.Current.Hosts.Select(h => h.Alias));

        File.AppendAllText(config, "Host second\n  User ops\n");
        catalog.Refresh();
        Assert.Equal(["first", "second"], catalog.Current.Hosts.Select(h => h.Alias));
        Assert.Equal("ops", catalog.Current.Hosts[1].User);
        Assert.True(catalog.Current.ConfigExists);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_folder_where_the_config_file_should_be_gives_no_hosts_instead_of_throwing()
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-").FullName;
        Directory.CreateDirectory(Path.Combine(home, ".ssh", "config"));

        var catalog = new SshHostCatalog(home);

        Assert.False(catalog.Current.ConfigExists);
        Assert.Empty(catalog.Current.Hosts);
    }

    [Fact]
    public void Add_puts_the_host_before_the_defaults_and_the_parser_reads_its_own_options_back()
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-add-").FullName;
        var config = Path.Combine(home, ".ssh", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "Host *\n  User bob\n");
        var catalog = new SshHostCatalog(home);
        var raised = 0;
        catalog.Changed += (_, _) => raised++;

        catalog.Add(new SshHostDraft("box", HostName: "10.0.0.1", User: "root", Port: "2222"));

        var box = Assert.Single(catalog.Current.Hosts, h => h.Alias == "box");
        Assert.Equal("root", box.User);
        Assert.Equal(2222, box.Port);
        Assert.Equal("Host box\n    HostName 10.0.0.1\n    User root\n    Port 2222\n\nHost *\n  User bob\n", File.ReadAllText(config));
        Assert.True(raised >= 1);
    }

    [Fact]
    public void Add_creates_the_folder_and_the_file_when_they_are_missing()
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-new-").FullName;
        var catalog = new SshHostCatalog(home);

        catalog.Add(new SshHostDraft("first"));

        Assert.True(catalog.Current.ConfigExists);
        Assert.Equal(["first"], catalog.Current.Hosts.Select(h => h.Alias));
    }

    [Fact]
    public void Add_refuses_a_draft_that_is_not_valid_and_leaves_the_file_alone()
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-bad-").FullName;
        var config = Path.Combine(home, ".ssh", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "Host a\n");
        var catalog = new SshHostCatalog(home);

        Assert.Throws<ArgumentException>(() => catalog.Add(new SshHostDraft("a")));
        Assert.Throws<ArgumentException>(() => catalog.Add(new SshHostDraft("b", Port: "99999")));

        Assert.Equal("Host a\n", File.ReadAllText(config));
    }

    [Fact]
    public void A_write_that_cannot_be_done_throws_and_changes_nothing()
    {
        var home = Directory.CreateTempSubdirectory("octopus-ssh-fail-").FullName;
        // A file where the folder should be: the folder cannot be created.
        File.WriteAllText(Path.Combine(home, ".ssh"), "not a folder");
        var catalog = new SshHostCatalog(home);

        Assert.ThrowsAny<IOException>(() => catalog.Add(new SshHostDraft("box")));
        Assert.Equal("not a folder", File.ReadAllText(Path.Combine(home, ".ssh")));
    }
}
