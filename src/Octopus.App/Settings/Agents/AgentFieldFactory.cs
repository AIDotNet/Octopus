using Octopus.App.Localization;
using Octopus.Core.Agents;

namespace Octopus.App.Settings.Agents;

/// <summary>
/// Turns an agent's capability blocks into field view models: a block that is absent gives no field. Adding a new
/// kind of block means adding a field view model, a template and one case here.
/// </summary>
public static class AgentFieldFactory
{
    public static List<AgentFieldViewModel> Create(AgentRowViewModel row)
    {
        var def = row.Definition;
        var caps = def.Capabilities;
        var fields = new List<AgentFieldViewModel>
        {
            new CommandFieldViewModel(row, Text(AppLangResourceKind.SettingsFieldCommand), row.DefaultCommandName)
        };

        if (caps.Model is { } model)
        {
            var field = new ChoiceFieldViewModel(
                row, "model", Text(AppLangResourceKind.SettingsFieldModel),
                Text(AppLangResourceKind.SettingsAgentDefault),
                ModelItems(model.Options, []),
                () => row.Profile.Model,
                value =>
                {
                    row.Mutate(p => p.Model = value);
                    row.OnModelChanged();
                },
                allowCustom: model.AllowCustom,
                customLabel: Text(AppLangResourceKind.SettingsCustomOption),
                customPlaceholder: Text(AppLangResourceKind.SettingsCustomModelPlaceholder));
            row.ModelField = field;
            fields.Add(field);
        }

        if (caps.Effort is { } effort)
        {
            var label = string.IsNullOrWhiteSpace(effort.Label) ? Text(AppLangResourceKind.SettingsFieldEffort) : effort.Label!;
            var field = new ChoiceFieldViewModel(
                row, "effort", label, Text(AppLangResourceKind.SettingsOptionDefault),
                row.EffortItems(),
                () => row.Profile.Effort,
                value => row.Mutate(p => p.Effort = value));
            row.EffortField = field;
            fields.Add(field);
        }

        if (caps.Fast is not null)
        {
            fields.Add(new ToggleFieldViewModel(
                row, "fast", Text(AppLangResourceKind.SettingsFieldFast), row.Profile.Fast == true,
                on => row.Mutate(p => p.Fast = on ? true : null)));
        }

        if (caps.Permission is { } permission)
        {
            var modes = PermissionModes.All.Where(permission.Modes.ContainsKey).ToList();
            fields.Add(new ChoiceFieldViewModel(
                row, "permission", Text(AppLangResourceKind.SettingsFieldPermission), "",
                modes.Select(id => (id, ModeLabel(permission, id))),
                () => row.Profile.Permission,
                value => row.Mutate(p => p.Permission = value),
                dangerIds: modes.Where(id => permission.Modes[id].Danger),
                defaultLabel: () => AppLangResources.Format(AppLangResourceKind.SettingsSameAsAll, row.EffectiveGlobalPermissionName()),
                defaultEffectiveId: row.EffectiveGlobalPermission));
        }

        if (caps.ExtraArgs)
        {
            fields.Add(new ArgsFieldViewModel(
                row, "extraArgs", Text(AppLangResourceKind.SettingsFieldArgs), [.. row.Profile.ExtraArgs],
                value => row.Mutate(p => p.ExtraArgs = [.. value]),
                Text(AppLangResourceKind.SettingsArgsPlaceholder)));
        }

        if (caps.Env is { } env)
        {
            fields.Add(new EnvFieldViewModel(row, Text(AppLangResourceKind.SettingsFieldEnv), env));
        }

        foreach (var feature in caps.Features)
        {
            fields.Add(FeatureField(row, feature));
        }
        return fields;
    }

    /// <summary>The effort levels offered for a model, with their display names.</summary>
    public static IEnumerable<(string Id, string Label)> EffortItems(IEnumerable<AgentOption> options) =>
        options.Select(o => (o.Id, PermissionNames.Effort(o.Id)));

    public static IEnumerable<(string Id, string Label)> ModelItems(IEnumerable<AgentOption> options, IEnumerable<AgentOption> discovered)
    {
        var seen = new HashSet<string>();
        foreach (var option in options.Concat(discovered))
        {
            if (seen.Add(option.Id))
            {
                yield return (option.Id, option.LabelFor(AgentLanguage.Current));
            }
        }
    }

    public static string ModeLabel(PermissionCapability capability, string id) =>
        capability.Modes.TryGetValue(id, out var spec) && spec.LabelFor(AgentLanguage.Current) is { } own ? own : PermissionNames.Of(id);

    private static AgentFieldViewModel FeatureField(AgentRowViewModel row, AgentFeature feature)
    {
        var key = "feature:" + feature.Id;
        var language = AgentLanguage.Current;
        var label = feature.LabelFor(language);
        var description = feature.DescriptionFor(language);
        var placeholder = feature.PlaceholderFor(language);
        var stored = row.Profile.Features.GetValueOrDefault(feature.Id) ?? "";

        void Write(string? value) => row.Mutate(p =>
        {
            if (string.IsNullOrEmpty(value))
            {
                p.Features.Remove(feature.Id);
            }
            else
            {
                p.Features[feature.Id] = value;
            }
        });

        switch (feature.Type)
        {
            case "bool":
                return new ToggleFieldViewModel(
                    row, key, label, string.Equals(stored.Trim(), "true", StringComparison.OrdinalIgnoreCase),
                    on => Write(on ? "true" : null), description);
            case "enum":
                return new ChoiceFieldViewModel(
                    row, key, label, Text(AppLangResourceKind.SettingsOptionDefault),
                    (feature.Options ?? []).Select(o => (o.Id, o.LabelFor(language))),
                    () => row.Profile.Features.GetValueOrDefault(feature.Id),
                    Write,
                    description: description);
            case "list":
                var items = stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return new ArgsFieldViewModel(
                    row, key, label, items,
                    value => Write(value.Count == 0 ? null : string.Join('\n', value)),
                    placeholder ?? Text(AppLangResourceKind.SettingsArgsPlaceholder),
                    description);
            default:
                var kind = feature.Type switch
                {
                    "text" => TextFieldKind.MultiLine,
                    "file" => TextFieldKind.File,
                    "directory" => TextFieldKind.Directory,
                    _ => TextFieldKind.SingleLine
                };
                return new TextFieldViewModel(row, key, label, kind, stored, Write, placeholder, description);
        }
    }

    private static string Text(AppLangResourceKind kind) => AppLangResources.Get(kind);
}
