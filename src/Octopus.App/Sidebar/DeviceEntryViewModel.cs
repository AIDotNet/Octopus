using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Projects;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Sidebar;

public enum DeviceConnectionStatus
{
    Local,
    Online,
    Connecting,
    Offline,
    Ssh,
}

/// <summary>
/// One selectable device in the sidebar switcher (this computer, a paired Octopus host, or an SSH host).
/// Supports the same custom appearance model as projects: solid color + initial / Lucide icon / built-in pixel icon,
/// or a cropped picture thumbnail.
/// </summary>
public sealed partial class DeviceEntryViewModel : ObservableObject
{
    private readonly Action<DeviceEntryViewModel> _select;
    private readonly Func<DeviceEntryViewModel, Task> _changeIcon;
    private readonly Func<DeviceEntryViewModel, Task>? _reconnect;

    public DeviceEntryViewModel(
        string key,
        ProjectLocationKind kind,
        string? targetId,
        string name,
        string subtitle,
        string statusText,
        DeviceConnectionStatus status,
        bool isCurrent,
        DeviceAppearance? customAppearance,
        string defaultColor,
        LucideIconKind defaultIcon,
        Bitmap? image,
        int projectCount,
        int waitingCount,
        bool isWorking,
        bool isDone,
        Action<DeviceEntryViewModel> select,
        Func<DeviceEntryViewModel, Task> changeIcon,
        Func<DeviceEntryViewModel, Task>? reconnect = null)
    {
        _select = select;
        _changeIcon = changeIcon;
        _reconnect = reconnect;

        Key = key;
        Kind = kind;
        TargetId = targetId;
        Name = name;
        Subtitle = subtitle;
        StatusText = statusText;
        Status = status;
        IsCurrent = isCurrent;
        DefaultColor = defaultColor;
        DefaultIcon = defaultIcon;
        Initial = InitialOf(name);
        ProjectCount = projectCount;
        WaitingCount = waitingCount;
        IsWorking = isWorking;
        IsDone = isDone;

        // Custom appearance mirrors ProjectChipViewModel:
        // - Uncustomized device: default color + default Lucide icon (Laptop / Monitor / Terminal).
        // - Customized device: chosen picture, or chosen color + (Lucide icon | pixel icon | initial when Icon is null).
        Image = image;
        var chosenColor = customAppearance?.Color ?? defaultColor;
        Background = image is not null
            ? Brushes.Transparent
            : (Color.TryParse(chosenColor, out var parsed) ? new SolidColorBrush(parsed) : Brushes.Gray);

        if (customAppearance is null)
        {
            IconKind = defaultIcon;
            Pixel = null;
        }
        else
        {
            IconKind = ProjectIcons.Parse(customAppearance.Icon);
            Pixel = PixelIcons.Parse(customAppearance.Icon);
        }

        ProjectCountText = projectCount == 1
            ? AppLangResources.Get(AppLangResourceKind.DeviceProjectCountOne)
            : AppLangResources.Format(AppLangResourceKind.DeviceProjectCount, projectCount);
    }

    public string Key { get; }
    public ProjectLocationKind Kind { get; }
    public string? TargetId { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public string StatusText { get; }
    public DeviceConnectionStatus Status { get; }
    public string DefaultColor { get; }
    public LucideIconKind DefaultIcon { get; }
    public string Initial { get; }

    public IBrush Background { get; }
    public Bitmap? Image { get; }
    public LucideIconKind? IconKind { get; }
    public LucideIconKind EffectiveIconKind => IconKind ?? DefaultIcon;
    public PixelIcon? Pixel { get; }

    public bool HasImage => Image is not null;
    public bool ShowIcon => !HasImage && IconKind is not null;
    public bool ShowPixel => !HasImage && Pixel is not null;
    public bool ShowInitial => !HasImage && IconKind is null && Pixel is null;

    public bool IsLocal => Kind == ProjectLocationKind.Local;
    public bool IsRemote => Kind == ProjectLocationKind.Remote;
    public bool IsSsh => Kind == ProjectLocationKind.Ssh;
    public bool IsOffline => Status == DeviceConnectionStatus.Offline;
    public bool IsConnecting => Status == DeviceConnectionStatus.Connecting;
    public bool IsOnlineStatus => Status is DeviceConnectionStatus.Local or DeviceConnectionStatus.Online;
    public bool IsSshStatus => Status == DeviceConnectionStatus.Ssh;
    public bool CanReconnect => IsRemote && IsOffline && _reconnect is not null;

    public double BadgeOpacity => IsOffline ? 0.58 : 1.0;

    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty] public partial int ProjectCount { get; set; }
    public string ProjectCountText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaiting), nameof(WaitingBadgeText), nameof(ShowWorkingIndicator))]
    public partial int WaitingCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorkingIndicator))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty] public partial bool IsDone { get; set; }

    public bool HasWaiting => WaitingCount > 0;
    public string WaitingBadgeText => WaitingCount > 9 ? "9+" : WaitingCount.ToString();
    public bool ShowWorkingIndicator => !HasWaiting && IsWorking;

    public string HeaderSummary => $"{Name} · {StatusText}";

    [RelayCommand] private void Select() => _select(this);
    [RelayCommand] private Task ChangeIconAsync() => _changeIcon(this);
    [RelayCommand(CanExecute = nameof(CanReconnect))] private Task ReconnectAsync() => _reconnect?.Invoke(this) ?? Task.CompletedTask;

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ConvertFromUtf32(char.ConvertToUtf32(name.Trim(), 0)).ToUpperInvariant();
}
