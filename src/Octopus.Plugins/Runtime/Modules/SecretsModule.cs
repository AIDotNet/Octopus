using Octopus.Platform;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>octopus.secrets</c> (permission <c>secrets</c>, design/plugins/11 §8.2): the plugin's own secrets in the system
/// keystore, under the service <c>Octopus/&lt;plugin id&gt;</c>. A plugin reaches only its own; the value travels in the
/// call's text, never in its JSON (so it is not logged with the arguments).
/// </summary>
public sealed class SecretsModule(ISecretStore store) : IPluginApiModule
{
    public const int MaxNameLength = 128;
    public const int MaxValueLength = 64 * 1024;

    public void Register(PluginApiRouter router)
    {
        router.Request("secrets.get", async (call, cancellationToken) =>
        {
            var (service, name) = Address(call);
            var value = await Run(() => store.GetAsync(service, name, cancellationToken)).ConfigureAwait(false);
            return value is null ? PluginReply.Of((System.Text.Json.Nodes.JsonNode?)null) : PluginReply.Of(value);
        });
        router.Request("secrets.set", async (call, cancellationToken) =>
        {
            var (service, name) = Address(call);
            if (call.Text.Length > MaxValueLength) throw PluginApiException.Invalid($"A secret is at most {MaxValueLength} characters.");
            await Run(async () =>
            {
                await store.SetAsync(service, name, call.Text, cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return PluginReply.Empty;
        });
        router.Request("secrets.delete", async (call, cancellationToken) =>
        {
            var (service, name) = Address(call);
            await Run(async () =>
            {
                await store.DeleteAsync(service, name, cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return PluginReply.Empty;
        });
    }

    /// <summary>The service of this plugin and the checked name.</summary>
    private static (string Service, string Name) Address(PluginCall call)
    {
        call.Plugin.Grants.Require("secrets");
        var name = call.RequiredString("name");
        if (name.Length > MaxNameLength || name.Any(char.IsControl)) throw PluginApiException.Invalid($"A secret's name is 1 to {MaxNameLength} characters, without control characters.");
        return ("Octopus/" + call.Plugin.Id, name);
    }

    private async Task<T> Run<T>(Func<Task<T>> action)
    {
        if (!store.IsAvailable) throw PluginApiException.Unsupported("A system keystore");
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            throw PluginApiException.Unsupported("A system keystore");
        }
        catch (IOException e)
        {
            throw new PluginApiException("Unavailable", e.Message);
        }
    }
}
