using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Localization;
using Octopus.App.Updates;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.General;

/// <summary>The general page: the language, whether agent sessions continue after a restart, and updates.</summary>
public sealed partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;

    public GeneralSettingsViewModel(ISettingsStore settings, UpdateSettingsViewModel updates)
    {
        _settings = settings;
        Updates = updates;
        Languages = [.. AppLanguages.All.Select(l => new SelectOption(l.Tag.ToString(), l.Name))];
    }

    public IReadOnlyList<SelectOption> Languages { get; }

    /// <summary>The "Updates" group: version, automatic updates, channel.</summary>
    public UpdateSettingsViewModel Updates { get; }

    /// <summary>The tag of the current language, for example <c>zh-CN</c>.</summary>
    public string Language
    {
        get
        {
            var saved = _settings.Current.Appearance.Language;
            return LanguageTag.TryParse(saved, out var tag) ? tag.ToString() : ArcLanguage.Current.ToString();
        }
        set
        {
            if (string.IsNullOrEmpty(value) || value == Language || !LanguageTag.TryParse(value, out var tag))
            {
                return;
            }
            ArcLanguage.Set(tag);
            _settings.Update(s => s.Appearance.Language = tag.ToString());
            OnPropertyChanged();
        }
    }

    public bool RestoreItemsOnStartup
    {
        get => _settings.Current.Terminal.RestoreItemsOnStartup;
        set
        {
            if (value == RestoreItemsOnStartup)
            {
                return;
            }
            _settings.Update(s => s.Terminal.RestoreItemsOnStartup = value);
            OnPropertyChanged();
        }
    }

    public bool ResumeAgentsOnRestart
    {
        get => _settings.Current.Terminal.ResumeAgentsOnRestart;
        set
        {
            if (value == ResumeAgentsOnRestart)
            {
                return;
            }
            _settings.Update(s => s.Terminal.ResumeAgentsOnRestart = value);
            OnPropertyChanged();
        }
    }

    public bool TrayEnabled
    {
        get => _settings.Current.Tray.Enabled;
        set
        {
            if (value == TrayEnabled)
            {
                return;
            }
            _settings.Update(s => s.Tray.Enabled = value);
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => _settings.Current.Tray.CloseToTray;
        set
        {
            if (value == CloseToTray)
            {
                return;
            }
            _settings.Update(s => s.Tray.CloseToTray = value);
            OnPropertyChanged();
        }
    }
}
