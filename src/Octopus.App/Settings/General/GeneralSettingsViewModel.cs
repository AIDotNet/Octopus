using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Localization;
using Octopus.Core.Settings;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.General;

/// <summary>The general page: language, launch at login, startup agent restore, and system tray.</summary>
public sealed partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IAutoStartService _autoStart;

    public GeneralSettingsViewModel(ISettingsStore settings, IAutoStartService autoStart)
    {
        _settings = settings;
        _autoStart = autoStart;
        Languages = [.. AppLanguages.All.Select(l => new SelectOption(l.Tag.ToString(), l.Name))];
        CloseActions = BuildCloseActions();
    }

    public IReadOnlyList<SelectOption> Languages { get; }

    [ObservableProperty] public partial IReadOnlyList<SelectOption> CloseActions { get; private set; }

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
            CloseActions = BuildCloseActions();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseAction));
        }
    }

    public bool LaunchAtLogin
    {
        get => _settings.Current.Startup.LaunchAtLogin;
        set
        {
            if (value == LaunchAtLogin)
            {
                return;
            }
            _settings.Update(s => s.Startup.LaunchAtLogin = value);
            if (_autoStart.IsSupported)
            {
                _autoStart.SetEnabled(value);
            }
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
        get => _settings.Current.Tray.EffectiveCloseAction() == TrayCloseActions.MinimizeToTray;
        set
        {
            if (value == CloseToTray)
            {
                return;
            }
            _settings.Update(s =>
            {
                s.Tray.CloseToTray = value;
                s.Tray.CloseAction = value ? TrayCloseActions.MinimizeToTray : TrayCloseActions.Quit;
            });
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseAction));
        }
    }

    public string CloseAction
    {
        get => _settings.Current.Tray.EffectiveCloseAction();
        set
        {
            if (value is not (TrayCloseActions.Ask or TrayCloseActions.MinimizeToTray or TrayCloseActions.Quit)
                || value == CloseAction)
            {
                return;
            }
            _settings.Update(s =>
            {
                s.Tray.CloseAction = value;
                s.Tray.CloseToTray = value == TrayCloseActions.MinimizeToTray;
            });
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseToTray));
        }
    }

    private static IReadOnlyList<SelectOption> BuildCloseActions() =>
    [
        new SelectOption(TrayCloseActions.Ask, AppLangResources.Get(AppLangResourceKind.SettingsTrayCloseAsk)),
        new SelectOption(TrayCloseActions.MinimizeToTray, AppLangResources.Get(AppLangResourceKind.SettingsTrayCloseMinimize)),
        new SelectOption(TrayCloseActions.Quit, AppLangResources.Get(AppLangResourceKind.SettingsTrayCloseQuit)),
    ];
}
