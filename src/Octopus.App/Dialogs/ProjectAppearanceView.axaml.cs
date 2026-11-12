using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Sidebar;
using Octopus.Core.Projects;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Dialogs;

/// <summary>
/// The form of the project icon dialog: a color, an icon (or the initial), or a picture. The icons are the Lucide line
/// icons and the built-in pixel icons, shown a category at a time or as the matches of a search. A picture is turned
/// into a thumbnail as soon as it is chosen, so the preview shows what will be kept; thumbnails made here and not kept
/// are deleted by <see cref="DiscardUnused"/>.
/// </summary>
public partial class ProjectAppearanceView : UserControl
{
    private readonly IPickerService? _pickers;
    private readonly IProjectIconStore? _icons;
    private readonly string _projectId;
    private readonly List<string> _imported = [];
    private readonly List<(Border Ring, string Color)> _swatches = [];
    private readonly List<(Avalonia.Controls.Button Button, string? Icon)> _iconButtons = [];
    private readonly List<(Avalonia.Controls.Button Button, string Key)> _categoryButtons = [];

    /// <summary>The key of the line icons among the categories.</summary>
    public const string LineCategory = "line";

    private string _category = LineCategory;

    public ProjectAppearanceView() : this(new Project { Name = "Project" }, null, null)
    {
    }

    public ProjectAppearanceView(Project project, IPickerService? pickers, IProjectIconStore? icons)
    {
        _pickers = pickers;
        _icons = icons;
        _projectId = project.Id;
        InitializeComponent();

        Color = project.Color ?? ProjectService.Palette[0];
        Icon = ProjectIcons.Parse(project.Icon) is { } kind ? kind.ToString()
            : PixelIcons.Parse(project.Icon) is { } pixel ? PixelIcons.Reference(pixel) : null;
        // Open on the category of the chosen icon.
        _category = PixelIcons.Parse(Icon)?.Category ?? LineCategory;
        Image = project.Image;
        ProjectNameText.Text = project.Name;
        ProjectPathText.Text = project.SshHost is { } host ? host + ":" + project.Path : project.Path;
        PreviewInitial.Text = InitialOf(project.Name);

        BuildColors();
        BuildCategories();
        ShowIcons();
        IconSearch.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Text")
            {
                ShowIcons();
            }
        };
        ChoosePictureButton.Click += OnChoosePicture;
        RemovePictureButton.Click += (_, _) => SetImage(null);
        Refresh();
    }

    public string Color { get; private set; }

    /// <summary>A Lucide icon name or a pixel icon ("pixel:t-rex"); null shows the initial.</summary>
    public string? Icon { get; private set; }

    /// <summary>The icons the grid shows now, after the initial ("Aa", null).</summary>
    public IReadOnlyList<string?> ShownIcons => _iconButtons.Select(b => b.Icon).ToList();

    /// <summary>Shows the icons of a category (<see cref="LineCategory"/> or one of <see cref="PixelIcons.Categories"/>).</summary>
    public void ShowCategory(string key)
    {
        _category = key;
        if (!string.IsNullOrEmpty(IconSearch.Text))
        {
            IconSearch.Text = "";
        }
        ShowIcons();
    }

    /// <summary>Shows the icons whose name contains the text, from every category.</summary>
    public void SearchIcons(string text) => IconSearch.Text = text;

    /// <summary>Chooses an icon (null: the initial); a picture is dropped, as when a button of the grid is clicked.</summary>
    public void SelectIcon(string? icon)
    {
        Icon = icon;
        SetImage(null);
    }

    /// <summary>The thumbnail file shown; null for none.</summary>
    public string? Image { get; private set; }

    public ProjectAppearanceChoice Choice => new(Color, Icon, Image);

    /// <summary>Deletes the thumbnails made in this dialog that are not <paramref name="kept"/>.</summary>
    public void DiscardUnused(string? kept)
    {
        foreach (var name in _imported)
        {
            if (name != kept) _icons?.Delete(name);
        }
        _imported.Clear();
    }

    private void BuildColors()
    {
        foreach (var color in ProjectService.Palette)
        {
            var swatch = new Avalonia.Controls.Button
            {
                Classes = { "chip" },
                Width = 24,
                Height = 24,
                Background = new SolidColorBrush(Avalonia.Media.Color.Parse(color)),
            };
            AutomationProperties.SetName(swatch, color);
            swatch.Click += (_, _) =>
            {
                Color = color;
                // A color is for the initial or the icon: choosing one leaves the picture.
                SetImage(null);
            };
            var ring = new Border { Classes = { "ring" }, Child = swatch };
            _swatches.Add((ring, color));
            ColorPanel.Children.Add(ring);
        }
    }

    private void BuildCategories()
    {
        AddCategoryButton(LineCategory, AppLangResourceKind.IconCategoryLine);
        foreach (var key in PixelIcons.Categories)
        {
            if (PixelIcons.All.Any(i => i.Category == key))
            {
                AddCategoryButton(key, key switch
                {
                    "fun" => AppLangResourceKind.IconCategoryFun,
                    "nature" => AppLangResourceKind.IconCategoryNature,
                    "life" => AppLangResourceKind.IconCategoryLife,
                    "work" => AppLangResourceKind.IconCategoryWork,
                    "dev" => AppLangResourceKind.IconCategoryDev,
                    "brands" => AppLangResourceKind.IconCategoryBrands,
                    "letters" => AppLangResourceKind.IconCategoryLetters,
                    _ => AppLangResourceKind.IconCategoryOther,
                });
            }
        }
    }

    private void AddCategoryButton(string key, AppLangResourceKind title)
    {
        var button = new Avalonia.Controls.Button
        {
            Classes = { "row" },
            Height = 24,
            Padding = new Thickness(6, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Content = new TextBlock { Classes = { "caption" }, Text = AppLangResources.Get(title), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        };
        button.Click += (_, _) => ShowCategory(key);
        _categoryButtons.Add((button, key));
        CategoryPanel.Children.Add(button);
    }

    /// <summary>Fills the grid: the initial, then the matches of the search, or the icons of the category.</summary>
    private void ShowIcons()
    {
        IconPanel.Children.Clear();
        _iconButtons.Clear();
        AddIconButton(null, new TextBlock
        {
            Text = "Aa",
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        }, AppLangResources.Get(AppLangResourceKind.ProjectAppearanceInitial));

        var query = IconSearch.Text?.Trim() ?? "";
        var searching = query.Length > 0;
        bool Matches(string name) => name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || name.Replace('-', ' ').Contains(query, StringComparison.OrdinalIgnoreCase);
        foreach (var kind in ProjectIcons.Offered)
        {
            if (searching ? Matches(kind.ToString()) : _category == LineCategory)
            {
                AddIconButton(kind.ToString(), new LucideIcon { Kind = kind, Size = 16 }, kind.ToString());
            }
        }
        foreach (var pixel in PixelIcons.All)
        {
            if (searching ? Matches(pixel.Name) : pixel.Category == _category)
            {
                AddPixelButton(pixel);
            }
        }
        NoIconText.IsVisible = searching && _iconButtons.Count == 1;
        foreach (var (button, key) in _categoryButtons)
        {
            button.Classes.Set("selected", !searching && key == _category);
        }
        IconScroller.Offset = default;
        Refresh();
    }

    private void AddPixelButton(PixelIcon pixel)
    {
        var view = new PixelIconView { Icon = pixel, Width = 24, Height = 24 };
        var button = AddIconButton(PixelIcons.Reference(pixel), view, pixel.Name);
        // The glyph takes the button's text color, which follows the theme.
        view.Bind(PixelIconView.ForegroundProperty, button.GetObservable(TemplatedControl.ForegroundProperty));
    }

    private Avalonia.Controls.Button AddIconButton(string? icon, Control content, string name)
    {
        var button = new Avalonia.Controls.Button { Classes = { "row" }, Width = 32, Height = 32, Content = content, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        AutomationProperties.SetName(button, name);
        Tooltip.SetTip(button, name);
        button.Click += (_, _) => SelectIcon(icon);
        _iconButtons.Add((button, icon));
        IconPanel.Children.Add(button);
        return button;
    }

    private async void OnChoosePicture(object? sender, RoutedEventArgs e) => await ChoosePictureAsync();

    /// <summary>Asks for an image and makes its thumbnail; the preview shows it, or the error says why not.</summary>
    public async Task ChoosePictureAsync()
    {
        if (_pickers is null || _icons is null)
        {
            return;
        }
        var path = await _pickers.PickImageAsync();
        if (path is null)
        {
            return;
        }
        ChoosePictureButton.IsEnabled = false;
        try
        {
            var name = await _icons.ImportAsync(_projectId, path);
            if (name is null)
            {
                ShowPictureError(AppLangResources.Get(AppLangResourceKind.ProjectAppearancePictureFailed));
                return;
            }
            _imported.Add(name);
            ShowPictureError(null);
            SetImage(name);
        }
        finally
        {
            ChoosePictureButton.IsEnabled = true;
        }
    }

    private void SetImage(string? image)
    {
        Image = image;
        Refresh();
    }

    private void ShowPictureError(string? message)
    {
        PictureError.Text = message;
        PictureError.IsVisible = message is not null;
    }

    private void Refresh()
    {
        var bitmap = _icons?.Load(Image);
        var kind = ProjectIcons.Parse(Icon);
        var pixel = PixelIcons.Parse(Icon);
        Preview.Background = new SolidColorBrush(Avalonia.Media.Color.TryParse(Color, out var c) ? c : Colors.Gray);
        PreviewImage.Source = bitmap;
        PreviewImage.IsVisible = bitmap is not null;
        PreviewIcon.IsVisible = bitmap is null && kind is not null;
        if (kind is { } k) PreviewIcon.Kind = k;
        PreviewPixel.Icon = pixel;
        PreviewPixel.IsVisible = bitmap is null && pixel is not null;
        PreviewInitial.IsVisible = bitmap is null && kind is null && pixel is null;
        RemovePictureButton.IsEnabled = Image is not null;
        foreach (var (ring, color) in _swatches)
        {
            ring.Classes.Set("selected", bitmap is null && string.Equals(color, Color, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var (button, icon) in _iconButtons)
        {
            button.Classes.Set("selected", bitmap is null && icon == Icon);
        }
    }

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(name.Trim(), 0)).ToUpperInvariant();
}
