using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Threading;
using Octopus.Core.Plugins;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Themes;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.App.Plugins;

/// <summary>A color theme a plugin offers: from its manifest, or registered at run time.</summary>
public sealed record PluginThemeOption(string Key, string PluginId, string Label, ThemeAppearance Appearance, Func<ThemeDefinition?> Load);

/// <summary>
/// Applies plugin color themes (design/plugins/07-主题.md): the theme chosen for light and the one for dark, then the
/// runtime overrides plugins set, are composed (<see cref="ThemeComposer"/>) into one Arc preset with a light and a
/// dark half, so switching appearance needs nothing from here. A theme whose text would be unreadable is refused and
/// the previous colors stay. Accent, density and corners stay the user's (the appearance page).
/// </summary>
public sealed class PluginThemes
{
    public const string PresetName = "octopus.plugin-theme";

    private readonly IPluginCatalog _catalog;
    private readonly IPluginStateStore _state;
    private readonly ConcurrentDictionary<string, PluginThemeOption> _registered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ThemeDefinition> _overrides = new(StringComparer.Ordinal);
    private bool _applyQueued;

    public PluginThemes(IPluginCatalog catalog, IPluginStateStore state)
    {
        _catalog = catalog;
        _state = state;
        _catalog.Changed += (_, _) => QueueApply();
        _state.Changed += (_, _) => QueueApply();
    }

    /// <summary>Why the last theme was refused or partly applied; null when it applied cleanly.</summary>
    public string? Problem { get; private set; }

    public event EventHandler? Changed;

    public IReadOnlyList<PluginThemeOption> Available
    {
        get
        {
            var list = new List<PluginThemeOption>();
            foreach (var package in _catalog.Packages.Where(p => p.Status == PluginStatus.Enabled && p.Manifest is not null))
            {
                foreach (var theme in package.Manifest!.Contributes.Themes)
                {
                    var path = Path.Combine(package.Directory, theme.Path);
                    list.Add(new PluginThemeOption($"{package.Id}/{theme.Id}", package.Id,
                        theme.Localized.Pick(PluginText.Language, "label") ?? theme.Label, theme.Appearance,
                        () => ThemeReader.ReadFile(path).Theme));
                }
            }
            list.AddRange(_registered.Values);
            return list;
        }
    }

    public PluginThemeChoice Choice => _state.Current.Themes;

    public void Choose(ThemeAppearance appearance, string? key) => _state.Update(d =>
    {
        if (appearance == ThemeAppearance.Light) d.Themes.Light = key;
        else d.Themes.Dark = key;
    });

    // ---------------------------------------------------------------- run-time themes from plugins

    public void Register(string pluginId, string id, JsonNode? definition, string? label)
    {
        if (ParseDefinition(definition) is not { } theme) return;
        var key = $"{pluginId}/{id}";
        _registered[key] = new PluginThemeOption(key, pluginId, label ?? theme.Name ?? id, theme.Appearance ?? ThemeAppearance.Dark, () => theme);
        QueueApply();
    }

    public void Unregister(string pluginId, string id)
    {
        if (_registered.TryRemove($"{pluginId}/{id}", out _)) QueueApply();
    }

    public void SetOverride(string pluginId, string key, JsonNode? definition)
    {
        if (ParseDefinition(definition) is not { } theme) return;
        _overrides[$"{pluginId}#{key}"] = theme;
        QueueApply();
    }

    public void RemoveOverride(string pluginId, string key)
    {
        if (_overrides.TryRemove($"{pluginId}#{key}", out _)) QueueApply();
    }

    /// <summary>A plugin stopped: its run-time themes and overrides go with it.</summary>
    public void Forget(string pluginId)
    {
        var changed = false;
        foreach (var key in _registered.Keys.Where(k => k.StartsWith(pluginId + "/", StringComparison.Ordinal))) changed |= _registered.TryRemove(key, out _);
        foreach (var key in _overrides.Keys.Where(k => k.StartsWith(pluginId + "#", StringComparison.Ordinal))) changed |= _overrides.TryRemove(key, out _);
        if (changed) QueueApply();
    }

    private static ThemeDefinition? ParseDefinition(JsonNode? definition) =>
        definition is null ? null : ThemeReader.Parse(definition.ToJsonString()).Theme;

    // ---------------------------------------------------------------- applying

    private void QueueApply()
    {
        if (_applyQueued) return;
        _applyQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _applyQueued = false;
            Apply();
        });
    }

    /// <summary>Composes and applies the chosen themes now (UI thread).</summary>
    public void Apply()
    {
        var available = Available;
        ThemeDefinition? Load(string? key) => key is null ? null : available.FirstOrDefault(o => o.Key == key)?.Load();
        var light = ThemeComposer.Compose(ThemeAppearance.Light, Load(Choice.Light), UserAppearance.FollowTheme, [.. _overrides.Values], BaseColors(false));
        var dark = ThemeComposer.Compose(ThemeAppearance.Dark, Load(Choice.Dark), UserAppearance.FollowTheme, [.. _overrides.Values], BaseColors(true));
        var refused = new[] { light, dark }.Where(c => !c.IsAcceptable).ToList();
        if (refused.Count > 0)
        {
            Problem = string.Join("; ", refused.SelectMany(c => c.Problems.Where(p => p.Blocking))
                .Select(p => PluginText.Get($"{p.Foreground} 与 {p.Background} 的对比度只有 {p.Ratio}:1（至少 {p.Required}:1）", $"{p.Foreground} on {p.Background} is only {p.Ratio}:1 (at least {p.Required}:1)")));
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        var empty = light.Colors.Count == 0 && dark.Colors.Count == 0 && light.Controls.Count == 0 && dark.Controls.Count == 0;
        try
        {
            ArcTheme.Apply(b =>
            {
                b.RemovePreset(PresetName);
                if (!empty) b.Preset(Preset(light, dark, withControls: true));
            });
            Problem = null;
        }
        catch (ArcThemeException e)
        {
            // A control token Arc does not know: keep the colors, drop the per-control part.
            try
            {
                ArcTheme.Apply(b => b.RemovePreset(PresetName).Preset(Preset(light, dark, withControls: false)));
                Problem = PluginText.Get("主题里有 Arc 不认识的控件设置，已忽略：", "The theme has control settings Arc does not know; they were ignored: ") + e.Message;
            }
            catch (ArcThemeException inner)
            {
                ArcTheme.Apply(b => b.RemovePreset(PresetName));
                Problem = inner.Message;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ArcThemePreset Preset(ComposedTheme light, ComposedTheme dark, bool withControls) =>
        ArcThemePreset.Create(PresetName, p =>
        {
            p.ForLight(l => Fill(l, light, withControls));
            p.ForDark(d => Fill(d, dark, withControls));
        }, slot: PresetName); // its own slot: RemovePreset goes by slot

    private static void Fill(ArcThemePresetBuilder builder, ComposedTheme theme, bool withControls)
    {
        foreach (var (name, value) in theme.Colors)
        {
            // Only names Arc has: the reader already warned about the others.
            if (Enum.TryParse<ArcDesignTokenKind>(name, ignoreCase: false, out _)) builder.Token(name, value);
        }
        if (!withControls) return;
        foreach (var (control, tokens) in theme.Controls)
        {
            foreach (var (name, value) in tokens) builder.Token(control, name, value);
        }
    }

    private static string Hex(Avalonia.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Arc's colors for an appearance as they are now, for the contrast check of unset tokens.</summary>
    private static IReadOnlyDictionary<string, string>? BaseColors(bool dark)
    {
        if (Application.Current is not { } app || ArcTheme.Manager.IsDark != dark) return null;
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in new[] { ArcDesignTokenKind.Foreground, ArcDesignTokenKind.Background, ArcDesignTokenKind.Surface, ArcDesignTokenKind.TextSecondary, ArcDesignTokenKind.Accent, ArcDesignTokenKind.AccentForeground })
        {
            if (!app.TryGetResource(ArcDesignTokens.Key(kind), app.ActualThemeVariant, out var value)) continue;
            // #RRGGBB: Avalonia writes #AARRGGBB, which a theme file would read as #RRGGBBAA.
            if (value is Avalonia.Media.ISolidColorBrush brush) colors[kind.ToString()] = Hex(brush.Color);
            else if (value is Avalonia.Media.Color color) colors[kind.ToString()] = Hex(color);
        }
        return colors;
    }
}
