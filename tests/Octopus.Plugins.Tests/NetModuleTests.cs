using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Xunit;

namespace Octopus.Plugins.Tests;

public sealed class NetModuleTests
{
    [Fact]
    public async Task Loopback_proxy_is_allowed_for_external_https_host_without_localhost_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? observedRequestLine = null;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            observedRequestLine = await reader.ReadLineAsync(ct);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
            var reply = "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(reply, ct);
        }, ct);

        var proxy = new WebProxy($"http://127.0.0.1:{port}");
        using var module = new NetModule(proxy);

        var ex = await Assert.ThrowsAsync<PluginApiException>(() =>
            module.FetchCoreAsync("cowork.device-login", ["nextco.work"], "https://nextco.work/api/client/oauth/device", "POST", null, null, ct));

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal("CONNECT nextco.work:443 HTTP/1.1", observedRequestLine);
        Assert.Equal("Io", ex.Code);
        Assert.DoesNotContain("internal addresses", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direct_connection_to_host_resolving_to_internal_address_is_still_blocked()
    {
        var ct = TestContext.Current.CancellationToken;
        var noProxy = new BypassAllProxy();
        using var module = new NetModule(noProxy, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        var ex = await Assert.ThrowsAsync<PluginApiException>(() =>
            module.FetchCoreAsync("acme.test", ["evil.example"], "https://evil.example/", "GET", null, null, ct));

        Assert.Equal("Io", ex.Code);
        Assert.Contains("internal addresses", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://127.0.0.1/", "127.0.0.1")]
    [InlineData("https://169.254.169.254/latest/meta-data/", "169.254.169.254")]
    [InlineData("https://10.0.0.1/", "10.0.0.1")]
    [InlineData("https://[::1]/", "[::1]")]
    public async Task Proxied_connection_still_blocks_internal_target_hosts_without_localhost_grant(string url, string grantedHost)
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var proxy = new StaticProxy(new Uri($"http://127.0.0.1:{port}"));
        using var module = new NetModule(proxy);

        var ex = await Assert.ThrowsAsync<PluginApiException>(() =>
            module.FetchCoreAsync("acme.test", [grantedHost], url, "GET", null, null, ct));

        Assert.Equal("Io", ex.Code);
        Assert.Contains("internal addresses", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("CONNECT")]
    [InlineData("TRACE")]
    [InlineData("TRACK")]
    public async Task Forbidden_http_methods_are_rejected(string method)
    {
        var ct = TestContext.Current.CancellationToken;
        using var module = new NetModule(new BypassAllProxy());

        var ex = await Assert.ThrowsAsync<PluginApiException>(() =>
            module.FetchCoreAsync("acme.test", ["nextco.work"], "https://nextco.work/", method, null, null, ct));

        Assert.Equal("InvalidArgument", ex.Code);
    }

    [Fact]
    public async Task Explicit_localhost_grant_can_fetch_http_from_loopback()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
            var body = "{\"ok\":true}"u8.ToArray();
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
        }, ct);

        using var module = new NetModule(new BypassAllProxy());
        var reply = await module.FetchCoreAsync("acme.test", ["localhost"], $"http://localhost:{port}/ping", "GET", null, null, ct);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var json = JsonNode.Parse(reply.Json)!.AsObject();
        Assert.Equal(200, json["status"]!.GetValue<int>());
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(json["body"]!.GetValue<string>()));
        Assert.Equal("{\"ok\":true}", decoded);
    }

    [Fact]
    public void IsConfiguredProxy_matches_ipv4_ipv6_and_socks_endpoints_and_respects_bypass()
    {
        var target = new Uri("https://nextco.work/api");
        Assert.True(NetModule.IsConfiguredProxy(new StaticProxy(new Uri("http://127.0.0.1:10808")), target, new DnsEndPoint("127.0.0.1", 10808)));
        Assert.True(NetModule.IsConfiguredProxy(new StaticProxy(new Uri("http://[::1]:10808")), target, new DnsEndPoint("::1", 10808)));
        Assert.True(NetModule.IsConfiguredProxy(new StaticProxy(new Uri("socks5://127.0.0.1")), target, new DnsEndPoint("127.0.0.1", 1080)));
        Assert.False(NetModule.IsConfiguredProxy(new StaticProxy(new Uri("http://127.0.0.1:10808")), target, new DnsEndPoint("127.0.0.1", 9999)));
        Assert.False(NetModule.IsConfiguredProxy(new BypassAllProxy(), target, new DnsEndPoint("127.0.0.1", 10808)));

        using var tunnel = new HttpRequestMessage(HttpMethod.Connect, new Uri("http://127.0.0.1:10808/"));
        tunnel.Headers.Host = "nextco.work:443";
        Assert.True(NetModule.IsConfiguredProxyConnection(new StaticProxy(new Uri("http://127.0.0.1:10808")), tunnel, new DnsEndPoint("127.0.0.1", 10808)));
    }

    [Theory]
    [InlineData("https://localhost/", true)]
    [InlineData("https://sub.localhost/", true)]
    [InlineData("https://127.0.0.1/", true)]
    [InlineData("https://10.0.0.5/", true)]
    [InlineData("https://169.254.169.254/", true)]
    [InlineData("https://[fe80::1]/", true)]
    [InlineData("https://[::ffff:127.0.0.1]/", true)]
    [InlineData("https://nextco.work/", false)]
    [InlineData("https://43.167.193.168/", false)]
    public void IsInternalHost_identifies_internal_targets(string url, bool expected)
    {
        Assert.Equal(expected, NetModule.IsInternalHost(new Uri(url)));
    }

    private sealed class BypassAllProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => destination;
        public bool IsBypassed(Uri host) => true;
    }

    private sealed class StaticProxy(Uri proxyUri) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => proxyUri;
        public bool IsBypassed(Uri host) => false;
    }
}
