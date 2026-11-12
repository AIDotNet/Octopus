using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Settings;

/// <summary>One entry of the navigation on the left: its key, icon, name, and the page it opens (created when first shown).</summary>
public sealed partial class SettingsSectionViewModel : ObservableObject
{
    private readonly AppLangResourceKind _title;
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

    /// <summary><c>agents</c>, <c>general</c>, <c>terminal</c>, <c>appearance</c> or <c>shortcuts</c>.</summary>
    public string Key { get; }

    public LucideIconKind Icon { get; }

    public string Title => AppLangResources.Get(_title);

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
