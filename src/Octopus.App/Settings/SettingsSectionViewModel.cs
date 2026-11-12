using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Settings;

/// <summary>One entry of the navigation on the left: its key, icon, name, and the page it opens (created when first shown).</summary>
public sealed partial class SettingsSectionViewModel : ObservableObject
{
    private readonly AppLangResourceKind _title;
    private readonly Func<string>? _titleText;
    private readonly Func<object> _createPage;
    private object? _page;

    [ObservableProperty]
    private bool _isSelected;

    public SettingsSectionViewModel(string key, LucideIconKind icon, AppLangResourceKind title, Func<object> createPage)
    {
        Key = key;
        Icon = icon;
        _title = title;
        _createPage = createPage;
    }

    /// <summary>A section whose title is not in the app's string tables (the plugin pages while plugins are a preview).</summary>
    public SettingsSectionViewModel(string key, LucideIconKind icon, Func<string> title, Func<object> createPage)
        : this(key, icon, default(AppLangResourceKind), createPage)
    {
        _titleText = title;
    }

    /// <summary><c>agents</c>, <c>general</c>, <c>terminal</c>, <c>ssh</c>, <c>appearance</c>, <c>shortcuts</c> or <c>plugins</c>.</summary>
    public string Key { get; }

    public LucideIconKind Icon { get; }

    public string Title => _titleText?.Invoke() ?? AppLangResources.Get(_title);

    /// <summary>Opens this section; set by <see cref="SettingsViewModel"/>.</summary>
    public IRelayCommand<SettingsSectionViewModel>? SelectCommand { get; set; }

    public bool IsPageCreated => _page is not null;

    /// <summary>The view model of the page.</summary>
    public object Page => _page ??= _createPage();

    /// <summary>The language changed.</summary>
    public void RefreshTitle() => OnPropertyChanged(nameof(Title));

    public void DisposePage()
    {
        if (_page is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _page = null;
    }
}
