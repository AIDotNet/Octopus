namespace Octopus.Plugins.Runtime;

/// <summary>The source of the <c>octopus</c> module (Sdk/octopus.js, embedded).</summary>
public static class PluginSdk
{
    public const string ModuleId = "octopus";

    private static readonly Lazy<string> Text = new(() =>
    {
        using var stream = typeof(PluginSdk).Assembly.GetManifestResourceStream("Octopus.Plugins.Sdk.octopus.js")
            ?? throw new InvalidOperationException("The plugin SDK is not embedded in Octopus.Plugins.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Source => Text.Value;
}
