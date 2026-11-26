using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.Core.Bots;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots;

public sealed partial class ConnectorCategoryItemViewModel(
    string id,
    string name,
    LucideIconKind icon,
    int? count,
    bool isSelected,
    bool separatorBefore,
    Action<string> select) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public LucideIconKind Icon { get; } = icon;
    public int? Count { get; } = count;
    public bool HasCount => Count is > 0;
    public string CountText => Count?.ToString() ?? "";
    public bool SeparatorBefore { get; } = separatorBefore;

    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;

    [RelayCommand]
    private void Select() => select(Id);
}

public sealed partial class ConnectorAppRowViewModel : ObservableObject
{
    private readonly Action<ConnectorAppRowViewModel> _toggle;

    public ConnectorAppRowViewModel(
        ConnectorAppDefinition app,
        bool isAdded,
        Action<ConnectorAppRowViewModel> toggle)
    {
        AppId = app.Id;
        Name = app.Name;
        CategoryId = app.CategoryId;
        var isZh = ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        Description = isZh ? app.DescriptionZh : app.DescriptionEn;
        BadgeBrush = SolidColorBrush.Parse(app.AccentColor);
        BadgeText = app.BadgeText;
        Icon = NewBotDialogViewModel.ParseIcon(app.IconName, LucideIconKind.Plug);
        EndpointUrl = BotCatalog.ResolveMcpUrl(app);
        HasEndpoint = !string.IsNullOrWhiteSpace(EndpointUrl);
        AuthBadgeText = app.AuthMode switch
        {
            "none" => AppLangResources.Get(AppLangResourceKind.BotConnectorAuthNone),
            "bearer" => AppLangResources.Get(AppLangResourceKind.BotConnectorAuthTokenNeeded),
            _ => AppLangResources.Get(AppLangResourceKind.BotConnectorAuthOAuth),
        };
        IsAdded = isAdded;
        _toggle = toggle;
    }

    public string AppId { get; }
    public string Name { get; }
    public string CategoryId { get; }
    public string Description { get; }
    public IBrush BadgeBrush { get; }
    public string BadgeText { get; }
    public LucideIconKind Icon { get; }
    public string? EndpointUrl { get; }
    public bool HasEndpoint { get; }
    public string AuthBadgeText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    public partial bool IsAdded { get; set; }

    public string ActionLabel => AppLangResources.Get(IsAdded
        ? AppLangResourceKind.BotConnectorCatalogAdded
        : AppLangResourceKind.BotConnectorCatalogAdd);

    [RelayCommand]
    private void Toggle() => _toggle(this);
}

public sealed class ConnectorCatalogGroupViewModel(string header, LucideIconKind icon, IReadOnlyList<ConnectorAppRowViewModel> apps)
{
    public string Header { get; } = header;
    public LucideIconKind Icon { get; } = icon;
    public IReadOnlyList<ConnectorAppRowViewModel> Apps { get; } = apps;
}

/// <summary>ViewModel for the "Add to &lt;BotName&gt;" connector catalog dialog.</summary>
public sealed partial class ConnectorCatalogViewModel : ObservableObject
{
    private readonly string _botId;
    private readonly IBotService _bots;

    public ConnectorCatalogViewModel(BotDefinition bot, IBotService bots)
    {
        _botId = bot.Id;
        _bots = bots;
        BotName = bot.Name;
        SelectedCategoryId = "all";

        BuildCategories();
        RebuildGroups();
    }

    public string BotName { get; }
    public string DialogTitle => AppLangResources.Format(AppLangResourceKind.BotConnectorCatalogTitle, BotName);

    public ObservableCollection<ConnectorCategoryItemViewModel> Categories { get; } = [];
    public ObservableCollection<ConnectorCatalogGroupViewModel> Groups { get; } = [];
    public IReadOnlyList<SelectOption> TransportOptions { get; } =
    [
        new("http", "HTTP"),
        new("sse", "SSE"),
    ];

    [ObservableProperty] public partial string SelectedCategoryId { get; set; } = "all";
    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    [ObservableProperty] public partial bool IsAddingByUrl { get; set; }
    [ObservableProperty] public partial string CustomUrlName { get; set; } = "";
    [ObservableProperty] public partial string CustomUrl { get; set; } = "";
    [ObservableProperty] public partial string CustomTransport { get; set; } = "http";
    [ObservableProperty] public partial string CustomBearerToken { get; set; } = "";
    [ObservableProperty] public partial bool IsTestingCustomUrl { get; private set; }
    [ObservableProperty] public partial string CustomUrlTestStatus { get; private set; } = "";
    [ObservableProperty] public partial bool HasCustomUrlTestStatus { get; private set; }
    [ObservableProperty] public partial bool HasNoResults { get; private set; }

    partial void OnSearchQueryChanged(string value) => RebuildGroups();

    [RelayCommand]
    private void ToggleAddByUrl() => IsAddingByUrl = !IsAddingByUrl;

    [RelayCommand]
    private async Task TestCustomUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomUrl) || IsTestingCustomUrl) return;
        IsTestingCustomUrl = true;
        CustomUrlTestStatus = AppLangResources.Get(AppLangResourceKind.BotConnectorTesting);
        HasCustomUrlTestStatus = true;
        try
        {
            var result = await _bots.ConnectorRuntime.ProbeAsync(CustomUrl, CustomTransport, CustomBearerToken);
            CustomUrlTestStatus = result.Status switch
            {
                ConnectorProbeStatus.Reachable => AppLangResources.Format(AppLangResourceKind.BotConnectorTestOk, result.Message),
                ConnectorProbeStatus.AuthRequired => AppLangResources.Format(AppLangResourceKind.BotConnectorTestAuthRequired, result.Message),
                _ => AppLangResources.Format(AppLangResourceKind.BotConnectorTestFailed, result.Message),
            };
        }
        finally
        {
            IsTestingCustomUrl = false;
        }
    }

    [RelayCommand]
    private async Task AddByUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomUrl)) return;
        await _bots.AddCustomUrlConnectorAsync(_botId, CustomUrlName, CustomUrl, CustomTransport, CustomBearerToken);
        CustomUrlName = "";
        CustomUrl = "";
        CustomBearerToken = "";
        CustomUrlTestStatus = "";
        HasCustomUrlTestStatus = false;
        IsAddingByUrl = false;
    }

    private void BuildCategories()
    {
        Categories.Clear();
        var isZh = ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        foreach (var cat in BotCatalog.ConnectorCategories)
        {
            int? count = cat.Id is "all" or "composio"
                ? null
                : BotCatalog.ConnectorApps.Count(a => a.CategoryId == cat.Id);
            Categories.Add(new ConnectorCategoryItemViewModel(
                cat.Id,
                isZh ? cat.NameZh : cat.NameEn,
                NewBotDialogViewModel.ParseIcon(cat.IconName, LucideIconKind.Plug),
                count,
                isSelected: cat.Id == SelectedCategoryId,
                separatorBefore: cat.Id == "composio",
                select: SelectCategory));
        }
    }

    private void SelectCategory(string categoryId)
    {
        SelectedCategoryId = categoryId;
        foreach (var cat in Categories)
        {
            cat.IsSelected = cat.Id == categoryId;
        }
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == _botId);
        var addedIds = bot is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : bot.Connectors.Select(c => c.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var query = SearchQuery.Trim();
        var isZh = ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        var categoriesToShow = SelectedCategoryId is "all" or "composio"
            ? BotCatalog.ConnectorCategories.Where(c => c.Id is not ("all" or "composio"))
            : BotCatalog.ConnectorCategories.Where(c => c.Id == SelectedCategoryId);

        foreach (var cat in categoriesToShow)
        {
            var matched = BotCatalog.ConnectorApps
                .Where(a => a.CategoryId == cat.Id)
                .Where(a => query.Length == 0
                    || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || a.DescriptionZh.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || a.DescriptionEn.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(a => new ConnectorAppRowViewModel(a, addedIds.Contains(a.Id), ToggleApp))
                .ToList();

            if (matched.Count == 0) continue;

            Groups.Add(new ConnectorCatalogGroupViewModel(
                isZh ? cat.NameZh : cat.NameEn,
                NewBotDialogViewModel.ParseIcon(cat.IconName, LucideIconKind.Plug),
                matched));
        }

        HasNoResults = Groups.Count == 0;
    }

    private void ToggleApp(ConnectorAppRowViewModel row)
    {
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == _botId);
        if (bot is null) return;
        var existing = bot.Connectors.FirstOrDefault(c => string.Equals(c.AppId, row.AppId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _bots.RemoveConnector(_botId, existing.Id);
            row.IsAdded = false;
        }
        else
        {
            _bots.AddConnector(_botId, row.AppId);
            row.IsAdded = true;
        }
    }
}
