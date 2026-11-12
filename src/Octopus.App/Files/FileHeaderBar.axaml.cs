using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Octopus.App.Composition;
using Octopus.Platform;
using TKCode.Arc;

namespace Octopus.App.Files;

/// <summary>The info bar shared by every viewer and the diff tab: the path on the left, the viewer's own content and buttons (<see cref="Trailing"/>) and the common copy and reveal buttons on the right.</summary>
public partial class FileHeaderBar : UserControl
{
    public static readonly StyledProperty<string> FolderProperty = AvaloniaProperty.Register<FileHeaderBar, string>(nameof(Folder), "");
    public static readonly StyledProperty<string> FileNameProperty = AvaloniaProperty.Register<FileHeaderBar, string>(nameof(FileName), "");
    public static readonly StyledProperty<string> CopyTextProperty = AvaloniaProperty.Register<FileHeaderBar, string>(nameof(CopyText), "");
    public static readonly StyledProperty<string> RevealPathProperty = AvaloniaProperty.Register<FileHeaderBar, string>(nameof(RevealPath), "");
    public static readonly StyledProperty<Control?> TrailingProperty = AvaloniaProperty.Register<FileHeaderBar, Control?>(nameof(Trailing));

    public FileHeaderBar() => InitializeComponent();

    /// <summary>The folder part of the path, with its trailing slash; shown quieter than the name.</summary>
    public string Folder { get => GetValue(FolderProperty); set => SetValue(FolderProperty, value); }
    public string FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }

    /// <summary>What the copy button puts on the clipboard.</summary>
    public string CopyText { get => GetValue(CopyTextProperty); set => SetValue(CopyTextProperty, value); }

    /// <summary>What the reveal button shows in the file manager; the button hides when empty.</summary>
    public string RevealPath { get => GetValue(RevealPathProperty); set => SetValue(RevealPathProperty, value); }


    /// <summary>Content at the right, before the common buttons: each viewer puts its quiet facts and its own buttons here (wrap, zoom, save, the view switch).</summary>
    public Control? Trailing { get => GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FolderProperty) FolderRun.Text = Folder;
        else if (change.Property == FileNameProperty) NameRun.Text = FileName;
        else if (change.Property == TrailingProperty) TrailingHost.Content = Trailing;
        else if (change.Property == RevealPathProperty) RevealButton.IsVisible = RevealPath.Length > 0;
    }


    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(CopyText);
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (RevealPath.Length > 0) AppHost.Get<IFileOpener>().Reveal(RevealPath);
    }
}
