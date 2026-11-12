using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.Terminal;

/// <summary>The terminal page: shell, font, font size and scrollback. Text is saved once typing pauses.</summary>
public sealed partial class TerminalSettingsViewModel : ObservableObject, IDisposable
{
    public const string AutoShell = "auto";
    public const string CustomShell = "custom";

    private readonly ISettingsStore _settings;
    private readonly Debouncer _shellDebounce = new();
    private readonly Debouncer _fontDebounce = new();

    [ObservableProperty]
    private string _shellMode;

    [ObservableProperty]
    private string _shellPath;

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private string _detectedShellText;

    [ObservableProperty]
    private IReadOnlyList<SelectOption> _shellModes = [];

    private string? _detectedShellPath;

    public TerminalSettingsViewModel(ISettingsStore settings, IShellResolver shells)
    {
        _settings = settings;
        var terminal = settings.Current.Terminal;
        _shellMode = string.IsNullOrWhiteSpace(terminal.Shell) ? AutoShell : CustomShell;
        _shellPath = terminal.Shell ?? "";
        _fontFamily = terminal.FontFamily ?? "";
        _detectedShellText = AppLangResources.Get(AppLangResourceKind.SettingsShellDetecting);
        BuildShellModes();
        ArcLanguage.Changed += OnLanguageChanged;
        // The first call may block for a few dozen milliseconds (it can ask the login shell), so it runs off the UI thread.
        _ = Task.Run(() =>
        {
            string path;
            try
            {
                path = shells.GetDefaultShell().Path;
            }
            catch (Exception)
            {
                path = "";
            }
            Dispatcher.UIThread.Post(() =>
            {
                _detectedShellPath = path;
                DetectedShellText = AppLangResources.Format(AppLangResourceKind.SettingsShellDetected, path);
            });
        });
    }

    private void BuildShellModes() =>
        ShellModes =
        [
            new SelectOption(AutoShell, AppLangResources.Get(AppLangResourceKind.SettingsShellAuto)),
            new SelectOption(CustomShell, AppLangResources.Get(AppLangResourceKind.SettingsShellCustom))
        ];

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => UiContext.Run(() =>
    {
        BuildShellModes();
        OnPropertyChanged(nameof(ShellMode));
        DetectedShellText = AppLangResources.Format(
            _detectedShellPath is null ? AppLangResourceKind.SettingsShellDetecting : AppLangResourceKind.SettingsShellDetected,
            _detectedShellPath ?? "");
    });

    public void Dispose()
    {
        ArcLanguage.Changed -= OnLanguageChanged;
        FlushPending();
    }

    public bool IsCustomShell => ShellMode == CustomShell;

    public decimal FontSize
    {
        get => (decimal)_settings.Current.Terminal.FontSize;
        set
        {
            var clamped = Math.Clamp(value, 10, 24);
            if ((double)clamped == _settings.Current.Terminal.FontSize)
            {
                return;
            }
            _settings.Update(s => s.Terminal.FontSize = (double)clamped);
            OnPropertyChanged();
        }
    }

    public decimal ScrollbackLines
    {
        get => _settings.Current.Terminal.ScrollbackLines;
        set
        {
            var clamped = (int)Math.Clamp(value, 100, 100_000);
            if (clamped == _settings.Current.Terminal.ScrollbackLines)
            {
                return;
            }
            _settings.Update(s => s.Terminal.ScrollbackLines = clamped);
            OnPropertyChanged();
        }
    }

    public bool ConfirmMultilinePaste
    {
        get => _settings.Current.Terminal.ConfirmMultilinePaste;
        set
        {
            if (value == _settings.Current.Terminal.ConfirmMultilinePaste)
            {
                return;
            }
            _settings.Update(s => s.Terminal.ConfirmMultilinePaste = value);
            OnPropertyChanged();
        }
    }

    partial void OnShellModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomShell));
        if (value == AutoShell)
        {
            _shellDebounce.Cancel();
            _settings.Update(s => s.Terminal.Shell = null);
        }
        else
        {
            SaveShell();
        }
    }

    partial void OnShellPathChanged(string value)
    {
        if (IsCustomShell)
        {
            _shellDebounce.Schedule(SaveShell);
        }
    }

    partial void OnFontFamilyChanged(string value) => _fontDebounce.Schedule(() =>
    {
        var font = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settings.Update(s => s.Terminal.FontFamily = font);
    });

    /// <summary>Writes the text edits that are still waiting for typing to pause.</summary>
    public void FlushPending()
    {
        _shellDebounce.Flush();
        _fontDebounce.Flush();
    }

    private void SaveShell()
    {
        var path = string.IsNullOrWhiteSpace(ShellPath) ? null : ShellPath.Trim();
        _settings.Update(s => s.Terminal.Shell = path);
    }
}
