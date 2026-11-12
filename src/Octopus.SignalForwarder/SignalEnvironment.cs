namespace Octopus.SignalForwarder;

/// <summary>
/// What Octopus put in the terminal's environment when it started the agent. The forwarder runs as a child of the
/// agent, so it also sees variables the agent (or a wrapper command) changed, such as the account directory.
/// </summary>
internal sealed record SignalEnvironment(
    string? Endpoint,
    string? Token,
    string? ItemId,
    string? AccountVariable,
    string? Chain,
    IReadOnlyDictionary<string, string> All)
{
    public const string EndpointVariable = "OCTOPUS_SIGNAL_ENDPOINT";
    public const string TokenVariable = "OCTOPUS_SIGNAL_TOKEN";
    public const string ItemVariable = "OCTOPUS_ITEM_ID";
    /// <summary>The name of the variable that tells accounts apart, e.g. <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public const string AccountVariableName = "OCTOPUS_SIGNAL_ACCOUNT_VAR";
    /// <summary>The user's own command for a single slot (status line, notify), resolved by Octopus at launch.</summary>
    public const string ChainVariable = "OCTOPUS_SIGNAL_CHAIN";

    /// <summary>The value of the account variable as the agent sees it; empty when unset (the agent's default).</summary>
    public string Account =>
        AccountVariable is { Length: > 0 } name && All.TryGetValue(name, out var value) ? value : "";

    public static SignalEnvironment FromProcess()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            all[(string)entry.Key] = entry.Value as string ?? "";
        }
        return From(all);
    }

    public static SignalEnvironment From(IReadOnlyDictionary<string, string> all) => new(
        all.GetValueOrDefault(EndpointVariable),
        all.GetValueOrDefault(TokenVariable),
        all.GetValueOrDefault(ItemVariable),
        all.GetValueOrDefault(AccountVariableName),
        all.GetValueOrDefault(ChainVariable),
        all);
}
