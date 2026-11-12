using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Octopus.App.Workspace;

namespace Octopus.App.Files;

/// <summary>A read-only unified diff. Nothing runs in it, so closing it asks nothing.</summary>
public partial class DiffTabView : UserControl, ITabContent
{
    public DiffTabView() => InitializeComponent();

    public Task<bool> HasRunningChildAsync() => Task.FromResult(false);
    public Task CloseAsync() => Task.CompletedTask;
    public Task RestartAsync() => Task.CompletedTask;
    public void FocusContent() => Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // The terminal's font when the person chose one; otherwise the theme's monospace stack (the XAML default).
        if (DataContext is DiffTabViewModel { MonoFontFamily: { } family })
        {
            Lines.FontFamily = new FontFamily(family);
        }
        else
        {
            Lines.ClearValue(CodeView.FontFamilyProperty);
        }
    }
}
