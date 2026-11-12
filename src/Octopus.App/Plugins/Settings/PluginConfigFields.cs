using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Plugins;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using TKCode.Arc;

namespace Octopus.App.Plugins.Settings;

/// <summary>
/// One setting a plugin declares under <c>contributes.configuration</c>, edited on the plugin's row. Values go through
/// <see cref="PluginConfigStore"/>, the same store <c>octopus.config</c> reads, so the plugin hears the change.
/// </summary>
public abstract partial class PluginConfigField : ObservableObject
{
    private protected readonly PluginConfigStore _store;
    private protected readonly PluginManifest _manifest;
    private protected bool Loading;

    protected PluginConfigField(PluginConfigStore store, PluginManifest manifest, ConfigurationProperty property)
    {
        _store = store;
        _manifest = manifest;
        Property = property;
        Label = property.Localized.Pick(PluginText.Language, "label") ?? property.Label;
        Description = property.Localized.Pick(PluginText.Language, "description") ?? property.Description;
    }

    public ConfigurationProperty Property { get; }
    public string Label { get; }
    public string? Description { get; }

    [ObservableProperty] public partial string? Error { get; private set; }

    protected JsonNode? Read() => _store.Get(_manifest, Property.Key);

    protected void Write(JsonNode? value)
    {
        if (Loading) return;
        try
        {
            // The settings page is the user: it may write every kind, commands included.
            _store.Set(_manifest, Property.Key, value, byUser: true);
            Error = null;
        }
        catch (Exception e) when (e is PluginApiException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
    }

    /// <summary>The field for a declared setting; null for kinds edited elsewhere (secrets).</summary>
    public static PluginConfigField? Create(PluginConfigStore store, PluginManifest manifest, ConfigurationProperty property) => property.Type switch
    {
        ConfigurationType.Bool => new BoolConfigField(store, manifest, property),
        ConfigurationType.Number => new NumberConfigField(store, manifest, property),
        ConfigurationType.Enum => new ChoiceConfigField(store, manifest, property),
        ConfigurationType.List => new ListConfigField(store, manifest, property),
        ConfigurationType.Text => new TextConfigField(store, manifest, property, multiLine: true),
        ConfigurationType.Secret => null,
        ConfigurationType.Command => new CommandConfigField(store, manifest, property),
        _ => new TextConfigField(store, manifest, property, multiLine: false),
    };
}

public sealed partial class BoolConfigField : PluginConfigField
{
    public BoolConfigField(PluginConfigStore store, PluginManifest manifest, ConfigurationProperty property) : base(store, manifest, property)
    {
        Loading = true;
        Value = Read() is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        Loading = false;
    }

    [ObservableProperty] public partial bool Value { get; set; }
    partial void OnValueChanged(bool value) => Write(JsonValue.Create(value));
}

public sealed partial class NumberConfigField : PluginConfigField
{
    public NumberConfigField(PluginConfigStore store, PluginManifest manifest, ConfigurationProperty property) : base(store, manifest, property)
    {
        Loading = true;
        Value = Read() is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (decimal)d : 0;
        Loading = false;
    }

    [ObservableProperty] public partial decimal Value { get; set; }
    partial void OnValueChanged(decimal value) => Write(JsonValue.Create((double)value));
}

public sealed partial class TextConfigField : PluginConfigField
{
    public TextConfigField(PluginConfigStore store, PluginManifest manifest, ConfigurationProperty property, bool multiLine) : base(store, manifest, property)
    {
        IsMultiLine = multiLine;
        Loading = true;
        Value = Read() is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        Loading = false;
    }

    public bool IsMultiLine { get; }
    public bool IsSingleLine => !IsMultiLine;

    [ObservableProperty] public partial string Value { get; set; }
    partial void OnValueChanged(string value) => Write(JsonValue.Create(value));
}

public sealed partial class ChoiceConfigField : PluginConfigField
{
    public ChoiceConfigField(PluginConfigStore store, PluginManifest manifest, ConfigurationProperty property) : base(store, manifest, property)
    {
        Options = [.. property.Options.Select(o => new SelectOption(o, property.Localized.Pick(PluginText.Language, $"option.{o}") ?? o))];
        Loading = true;
        Value = Read() is JsonValue v && v.TryGetValue<string>(out var s) ? s : property.Options.FirstOrDefault() ?? "";
        Loading = false;
    }

    public IReadOnlyList<SelectOption> Options { get; }

    [ObservableProperty] public partial string Value { get; set; }
    partial void OnValueChanged(string value) => Write(JsonValue.Create(value));
}

public sealed partial class ListConfigField : PluginConfigField
{
    public ListConfigField(PluginConfigStore store, PluginManifest manifest, ConfigurationProperty property) : base(store, manifest, property)
    {
        Loading = true;
        Value = Read() is JsonArray a ? [.. a.Select(i => i?.ToString() ?? "").Where(s => s.Length > 0)] : [];
        Loading = false;
    }

    [ObservableProperty] public partial IReadOnlyList<string> Value { get; set; }

    partial void OnValueChanged(IReadOnlyList<string> value)
    {
        var array = new JsonArray();
        foreach (var item in value) array.Add((JsonNode)JsonValue.Create(item));
        Write(array);
    }
}

/// <summary>
/// A command line the user enters for the plugin to have run (a custom data source). The plugin cannot read or change
/// it; "测试" runs it once here and says what came back.
/// </summary>
public sealed partial class CommandConfigField : PluginConfigField
{
    public CommandConfigField(PluginConfigStore store, PluginManifest manifest, ConfigurationProperty property) : base(store, manifest, property)
    {
        Loading = true;
        // The user's own value only: a command never comes from the manifest.
        Value = store.GetUserValue(manifest, property.Key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        Loading = false;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial string Value { get; set; }

    partial void OnValueChanged(string value) => Write(value.Trim().Length == 0 ? null : JsonValue.Create(value));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResult { get; private set; }

    [ObservableProperty] public partial bool TestFailed { get; private set; }
    [ObservableProperty] public partial bool IsTesting { get; private set; }
    public bool HasTestResult => TestResult is not null;

    private bool CanTest() => Value.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        try
        {
            var result = await Composition.AppHost.Get<UserCommandRunner>().RunAsync(Value);
            var output = result.StandardOutput.Trim();
            var json = false;
            try
            {
                using var _ = JsonDocument.Parse(output);
                json = true;
            }
            catch (JsonException)
            {
            }
            var preview = output.Length > 200 ? output[..200] + "…" : output;
            (TestFailed, TestResult) = result switch
            {
                { TimedOut: true } => (true, PluginText.Get($"超过 {UserCommandRunner.Timeout.TotalSeconds:0} 秒没有结束。", $"Did not finish within {UserCommandRunner.Timeout.TotalSeconds:0} s.")),
                { ExitCode: not 0 } => (true, PluginText.Get($"退出码 {result.ExitCode}：", $"Exit code {result.ExitCode}: ") + (result.StandardError.Trim() is { Length: > 0 } e ? e : preview)),
                _ when !json => (true, PluginText.Get("输出不是 JSON：", "The output is not JSON: ") + preview),
                _ => (false, PluginText.Get($"成功（{result.ElapsedMilliseconds} 毫秒）：", $"OK ({result.ElapsedMilliseconds} ms): ") + preview),
            };
        }
        finally
        {
            IsTesting = false;
        }
    }
}
