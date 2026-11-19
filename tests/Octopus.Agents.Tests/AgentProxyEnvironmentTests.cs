namespace Octopus.Agents.Tests;

public class AgentProxyEnvironmentTests
{
    private static readonly Uri Proxy = new("http://127.0.0.1:10808/");

    [Fact]
    public void Sets_both_cases_and_a_local_no_proxy_list()
    {
        var added = AgentProxyEnvironment.Compute(Proxy, new Dictionary<string, string>());

        Assert.Equal("http://127.0.0.1:10808", added["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:10808", added["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:10808", added["http_proxy"]);
        Assert.Equal("http://127.0.0.1:10808", added["https_proxy"]);
        Assert.Equal(AgentProxyEnvironment.DefaultNoProxy, added["NO_PROXY"]);
        Assert.Equal(AgentProxyEnvironment.DefaultNoProxy, added["no_proxy"]);
    }

    [Fact]
    public void Adds_nothing_without_a_system_proxy()
    {
        Assert.Empty(AgentProxyEnvironment.Compute(null, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("HTTP_PROXY")]
    [InlineData("https_proxy")]
    [InlineData("ALL_PROXY")]
    public void Keeps_a_proxy_the_user_already_set(string name)
    {
        var added = AgentProxyEnvironment.Compute(Proxy, new Dictionary<string, string> { [name] = "http://corp:3128" });

        Assert.Empty(added);
    }

    [Fact]
    public void An_empty_proxy_variable_does_not_count_as_set()
    {
        var added = AgentProxyEnvironment.Compute(Proxy, new Dictionary<string, string> { ["HTTPS_PROXY"] = "  " });

        Assert.Equal("http://127.0.0.1:10808", added["HTTPS_PROXY"]);
    }

    [Fact]
    public void Keeps_the_users_no_proxy_list()
    {
        var added = AgentProxyEnvironment.Compute(Proxy, new Dictionary<string, string> { ["no_proxy"] = ".corp.example" });

        Assert.Equal(".corp.example", added["NO_PROXY"]);
        Assert.Equal(".corp.example", added["no_proxy"]);
    }

    [Fact]
    public void Drops_credentials_and_path_from_the_proxy_address()
    {
        var added = AgentProxyEnvironment.Compute(new Uri("http://user:pw@proxy.example:8080/pac"), new Dictionary<string, string>());

        Assert.Equal("http://proxy.example:8080", added["HTTP_PROXY"]);
    }
}
