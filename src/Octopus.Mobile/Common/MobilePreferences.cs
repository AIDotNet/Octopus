using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Octopus.Mobile.Common;

/// <summary>Theme override for the mobile app.</summary>
public enum MobileThemeMode
{
    System = 0,
    Dark = 1,
    Light = 2,
}

/// <summary>
/// Persisted mobile app preferences (appearance, background anti-freeze stream protection, terminal defaults).
/// Uses source-generated JSON serialization so it stays 100% NativeAOT-safe.
/// </summary>
public sealed class MobilePreferences
{
    private const string FileName = "mobile-prefs.json";
    private readonly object _gate = new();
    private string? _filePath;
    private MobileThemeMode _themeMode = MobileThemeMode.System;
    private bool _compactDensity = true;
    private bool _suspendStreamInBackground = true;
    private bool _snapshotResyncOnResume = true;
    private double _terminalFontSize = 12;
    private bool _terminalPasteConfirm = true;
    private bool _diagnosticsCollectionEnabled;

    public static MobilePreferences Current { get; } = new();

    public event Action? Changed;

    public MobileThemeMode ThemeMode
    {
        get => _themeMode;
        set
        {
            if (_themeMode == value) return;
            _themeMode = value;
            ApplyThemeToApplication(value);
            SaveAndNotify();
        }
    }

    public bool CompactDensity
    {
        get => _compactDensity;
        set
        {
            if (_compactDensity == value) return;
            _compactDensity = value;
            SaveAndNotify();
        }
    }

    /// <summary>When true, pauses live ACP streaming UI updates while the app is backgrounded to avoid wakeup freezes.</summary>
    public bool SuspendStreamInBackground
    {
        get => _suspendStreamInBackground;
        set
        {
            if (_suspendStreamInBackground == value) return;
            _suspendStreamInBackground = value;
            SaveAndNotify();
        }
    }

    /// <summary>When true, returning from background replaces stale streaming bursts with one clean snapshot sync.</summary>
    public bool SnapshotResyncOnResume
    {
        get => _snapshotResyncOnResume;
        set
        {
            if (_snapshotResyncOnResume == value) return;
            _snapshotResyncOnResume = value;
            SaveAndNotify();
        }
    }

    public double TerminalFontSize
    {
        get => _terminalFontSize;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 1), 8, 20);
            if (Math.Abs(_terminalFontSize - clamped) < 0.05) return;
            _terminalFontSize = clamped;
            SaveAndNotify();
        }
    }

    public bool TerminalPasteConfirm
    {
        get => _terminalPasteConfirm;
        set
        {
            if (_terminalPasteConfirm == value) return;
            _terminalPasteConfirm = value;
            SaveAndNotify();
        }
    }

    /// <summary>
    /// Opt-in consent for local crash and diagnostic log collection. Always defaults to <c>false</c> (disabled)
    /// until the user explicitly enables it.
    /// </summary>
    public bool DiagnosticsCollectionEnabled
    {
        get => _diagnosticsCollectionEnabled;
        set
        {
            if (_diagnosticsCollectionEnabled == value) return;
            _diagnosticsCollectionEnabled = value;
            Diagnostics.MobileDiagnostics.SetCollectionEnabled(value);
            SaveAndNotify();
        }
    }

    /// <summary>Loads persisted preferences from <paramref name="dataDirectory"/> if present and applies theme.</summary>
    public void Load(string? dataDirectory)
    {
        lock (_gate)
        {
            _filePath = string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, FileName);
            if (_filePath is not null && File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    if (JsonSerializer.Deserialize(json, MobilePreferencesJsonContext.Default.MobilePreferencesData) is { } data)
                    {
                        _themeMode = Enum.IsDefined(data.ThemeMode) ? data.ThemeMode : MobileThemeMode.System;
                        _compactDensity = data.CompactDensity;
                        _suspendStreamInBackground = data.SuspendStreamInBackground;
                        _snapshotResyncOnResume = data.SnapshotResyncOnResume;
                        _terminalFontSize = Math.Clamp(data.TerminalFontSize, 8, 20);
                        _terminalPasteConfirm = data.TerminalPasteConfirm;
                        _diagnosticsCollectionEnabled = data.DiagnosticsCollectionEnabled;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }
        }
        Diagnostics.MobileDiagnostics.SetCollectionEnabled(_diagnosticsCollectionEnabled);
        ApplyThemeToApplication(_themeMode);
        Changed?.Invoke();
    }

    /// <summary>Applies the active theme preference to Avalonia's root Application (safe from any thread).</summary>
    public void ApplyTheme() => ApplyThemeToApplication(_themeMode);

    /// <summary>Resets all preferences to defaults (useful for tests).</summary>
    internal void ResetToDefaults()
    {
        lock (_gate)
        {
            _filePath = null;
            _themeMode = MobileThemeMode.System;
            _compactDensity = true;
            _suspendStreamInBackground = true;
            _snapshotResyncOnResume = true;
            _terminalFontSize = 12;
            _terminalPasteConfirm = true;
            _diagnosticsCollectionEnabled = false;
        }
        Diagnostics.MobileDiagnostics.SetCollectionEnabled(false);
        ApplyThemeToApplication(_themeMode);
        Changed?.Invoke();
    }

    private void SaveAndNotify()
    {
        string? path;
        MobilePreferencesData snapshot;
        lock (_gate)
        {
            path = _filePath;
            snapshot = new MobilePreferencesData
            {
                ThemeMode = _themeMode,
                CompactDensity = _compactDensity,
                SuspendStreamInBackground = _suspendStreamInBackground,
                SnapshotResyncOnResume = _snapshotResyncOnResume,
                TerminalFontSize = _terminalFontSize,
                TerminalPasteConfirm = _terminalPasteConfirm,
                DiagnosticsCollectionEnabled = _diagnosticsCollectionEnabled,
            };
        }
        if (path is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(snapshot, MobilePreferencesJsonContext.Default.MobilePreferencesData);
                File.WriteAllText(path, json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        Changed?.Invoke();
    }

    private static void ApplyThemeToApplication(MobileThemeMode mode)
    {
        var variant = mode switch
        {
            MobileThemeMode.Dark => ThemeVariant.Dark,
            MobileThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
        if (Application.Current is null) return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            Application.Current.RequestedThemeVariant = variant;
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current is { } app) app.RequestedThemeVariant = variant;
            });
        }
    }
}

internal sealed class MobilePreferencesData
{
    public MobileThemeMode ThemeMode { get; set; } = MobileThemeMode.System;
    public bool CompactDensity { get; set; } = true;
    public bool SuspendStreamInBackground { get; set; } = true;
    public bool SnapshotResyncOnResume { get; set; } = true;
    public double TerminalFontSize { get; set; } = 12;
    public bool TerminalPasteConfirm { get; set; } = true;
    public bool DiagnosticsCollectionEnabled { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MobilePreferencesData))]
internal sealed partial class MobilePreferencesJsonContext : JsonSerializerContext;
