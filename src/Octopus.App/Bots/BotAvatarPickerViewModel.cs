using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Services;
using Octopus.Core.Bots;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Bots;

public sealed record BotAvatarChoice(string Color, string? Icon, string? Image);

/// <summary>ViewModel for the Bot Avatar Picker modal (matches Image 5: carousel of presets, palette swatches, and custom image upload).</summary>
public sealed partial class BotAvatarPickerViewModel : ObservableObject
{
    private readonly string _botId;
    private readonly IPickerService? _pickers;
    private readonly IProjectIconStore? _icons;
    private readonly List<string> _createdImages = [];
    private int _iconIndex;

    public BotAvatarPickerViewModel(BotDefinition bot, IPickerService? pickers = null, IProjectIconStore? icons = null)
    {
        _botId = bot.Id;
        _pickers = pickers;
        _icons = icons;
        Initial = bot.Name.Length > 0 ? bot.Name[..1].ToUpperInvariant() : "B";
        SelectedColor = bot.Color;
        SelectedImage = bot.Image;
        SelectedIcon = bot.Icon;

        var idx = -1;
        for (var i = 0; i < BotCatalog.AvatarIcons.Count; i++)
        {
            if (string.Equals(BotCatalog.AvatarIcons[i], bot.Icon ?? "", StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        _iconIndex = idx >= 0 ? idx : 0;

        foreach (var hex in BotCatalog.AvatarPalette)
        {
            Colors.Add(new BotColorOptionViewModel(hex, string.Equals(hex, SelectedColor, StringComparison.OrdinalIgnoreCase), SelectColor));
        }

        RefreshPreview();
    }

    public string Initial { get; }
    public ObservableCollection<BotColorOptionViewModel> Colors { get; } = [];

    [ObservableProperty] public partial string SelectedColor { get; set; } = "#93c5fd";
    [ObservableProperty] public partial string? SelectedIcon { get; set; }
    [ObservableProperty] public partial string? SelectedImage { get; set; }

    [ObservableProperty] public partial IBrush AvatarBrush { get; private set; } = SolidColorBrush.Parse("#93c5fd");
    [ObservableProperty] public partial Bitmap? PreviewImage { get; private set; }
    [ObservableProperty] public partial bool HasPreviewImage { get; private set; }
    [ObservableProperty] public partial bool HasCenterIcon { get; private set; }
    [ObservableProperty] public partial bool ShowCenterInitial { get; private set; }
    [ObservableProperty] public partial LucideIconKind CenterIcon { get; private set; } = LucideIconKind.Bot;
    [ObservableProperty] public partial LucideIconKind PrevIcon { get; private set; } = LucideIconKind.Sparkles;
    [ObservableProperty] public partial LucideIconKind NextIcon { get; private set; } = LucideIconKind.Cat;
    [ObservableProperty] public partial bool PrevIsInitial { get; private set; }
    [ObservableProperty] public partial bool NextIsInitial { get; private set; }

    public BotAvatarChoice Choice => new(SelectedColor, HasPreviewImage ? null : SelectedIcon, SelectedImage);

    [RelayCommand]
    private void Previous()
    {
        SelectedImage = null;
        _iconIndex = (_iconIndex - 1 + BotCatalog.AvatarIcons.Count) % BotCatalog.AvatarIcons.Count;
        SelectedIcon = string.IsNullOrEmpty(BotCatalog.AvatarIcons[_iconIndex]) ? null : BotCatalog.AvatarIcons[_iconIndex];
        RefreshPreview();
    }

    [RelayCommand]
    private void Next()
    {
        SelectedImage = null;
        _iconIndex = (_iconIndex + 1) % BotCatalog.AvatarIcons.Count;
        SelectedIcon = string.IsNullOrEmpty(BotCatalog.AvatarIcons[_iconIndex]) ? null : BotCatalog.AvatarIcons[_iconIndex];
        RefreshPreview();
    }

    [RelayCommand]
    private void ClearImage()
    {
        SelectedImage = null;
        RefreshPreview();
    }

    [RelayCommand]
    private async Task UploadImageAsync()
    {
        if (_pickers is null || _icons is null) return;
        var path = await _pickers.PickImageAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        var fileName = await _icons.ImportAsync("bot-" + _botId, path);
        if (string.IsNullOrWhiteSpace(fileName)) return;
        _createdImages.Add(fileName);
        SelectedImage = fileName;
        RefreshPreview();
    }

    public void DiscardUnused(string? keptImage)
    {
        if (_icons is null) return;
        foreach (var file in _createdImages)
        {
            if (!string.Equals(file, keptImage, StringComparison.Ordinal))
            {
                _icons.Delete(file);
            }
        }
    }

    private void SelectColor(string hex)
    {
        SelectedColor = hex;
        foreach (var c in Colors) c.IsSelected = string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        AvatarBrush = SolidColorBrush.Parse(SelectedColor);
        PreviewImage = _icons?.Load(SelectedImage);
        HasPreviewImage = PreviewImage is not null;

        var prevIdx = (_iconIndex - 1 + BotCatalog.AvatarIcons.Count) % BotCatalog.AvatarIcons.Count;
        var nextIdx = (_iconIndex + 1) % BotCatalog.AvatarIcons.Count;
        var prevName = BotCatalog.AvatarIcons[prevIdx];
        var nextName = BotCatalog.AvatarIcons[nextIdx];
        PrevIsInitial = string.IsNullOrEmpty(prevName);
        NextIsInitial = string.IsNullOrEmpty(nextName);
        PrevIcon = NewBotDialogViewModel.ParseIcon(prevName, LucideIconKind.Bot);
        NextIcon = NewBotDialogViewModel.ParseIcon(nextName, LucideIconKind.Bot);

        var centerName = SelectedIcon ?? "";
        HasCenterIcon = !HasPreviewImage && !string.IsNullOrEmpty(centerName);
        ShowCenterInitial = !HasPreviewImage && !HasCenterIcon;
        CenterIcon = NewBotDialogViewModel.ParseIcon(centerName, LucideIconKind.Bot);
    }
}
