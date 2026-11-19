namespace Octopus.Agents;

/// <summary>The proxy the operating system is configured to use.</summary>
public interface ISystemProxy
{
    /// <summary>The system proxy for web traffic, or null when there is none (or it cannot be read).</summary>
    Uri? Resolve();
}

/// <summary>
/// Reads the proxy through <see cref="HttpClient.DefaultProxy"/>, which knows the system settings on macOS and Windows
/// (and the environment variables on Linux), the same source the updater uses.
/// </summary>
public sealed class SystemProxy : ISystemProxy
{
    private static readonly Uri Probe = new("https://example.com/");

    public Uri? Resolve()
    {
        try
        {
            var proxy = HttpClient.DefaultProxy.GetProxy(Probe);
            // GetProxy returns the destination itself when the traffic goes direct.
            return proxy is null || proxy == Probe || !proxy.IsAbsoluteUri ? null : proxy;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Turns the system proxy into the environment variables that command-line agents read. Pure.</summary>
public static class AgentProxyEnvironment
{
    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];

    /// <summary>Traffic to this computer never goes through a proxy.</summary>
    public const string DefaultNoProxy = "localhost,127.0.0.1,::1";

    /// <summary>
    /// The variables to add so that an agent uses <paramref name="proxy"/>. Empty when there is no proxy, or when
    /// <paramref name="environment"/> (what the agent would start with) already names one: the user's own setting wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Compute(Uri? proxy, IReadOnlyDictionary<string, string> environment)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (proxy is null || IsConfigured(environment))
        {
            return result;
        }
        // Scheme and host:port only: credentials in a proxy address do not belong in an environment variable.
        var value = $"{proxy.Scheme}://{proxy.Authority}";
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY" })
        {
            result[name] = value;
            result[name.ToLowerInvariant()] = value;
        }
        var noProxy = Lookup(environment, "NO_PROXY") ?? DefaultNoProxy;
        result["NO_PROXY"] = noProxy;
        result["no_proxy"] = noProxy;
        return result;
    }

    private static bool IsConfigured(IReadOnlyDictionary<string, string> environment) =>
        ProxyVariables.Any(name => !string.IsNullOrWhiteSpace(Lookup(environment, name)));

    /// <summary>The value of a variable under either case (Windows ignores case, Unix does not).</summary>
    private static string? Lookup(IReadOnlyDictionary<string, string> environment, string name)
    {
        if (environment.TryGetValue(name, out var upper) && !string.IsNullOrWhiteSpace(upper))
        {
            return upper;
        }
        return environment.TryGetValue(name.ToLowerInvariant(), out var lower) && !string.IsNullOrWhiteSpace(lower) ? lower : null;
    }
}
