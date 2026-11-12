using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.Platform;
using Octopus.App.Workspace;

namespace Octopus.App.Terminals;

public partial class TerminalTabView : UserControl, ITabContent
{
    public TerminalTabView()
    {
        InitializeComponent();
        Terminal.ExitedMessageFormat = AppLangResources.Get(AppLangResourceKind.TerminalExited);
        var search = new TerminalSearchBar();
        search.Attach(Terminal);
        Terminal.SearchBar = search;
        Terminal.PasteConfirmer = (_, preview) => PasteConfirmation.ShowAsync(this, preview);
        Terminal.LinkActivated += (_, url) => AppHost.Get<IFileOpener>().OpenUrl(url);
    }

    private TerminalTabViewModel? Model => DataContext as TerminalTabViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyFont();
        if (Model is { } model)
        {
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(TerminalTabViewModel.TerminalFontFamily))
                {
                    ApplyFont();
                }
            };
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // First time on screen: only now does the process start (a restored tab that is never shown never runs).
        if (Model is { Started: false } model)
        {
            model.Started = true;
            if (model.TryCreateSpec() is { } spec)
            {
                Terminal.Start(spec, model.Factory);
            }
        }
    }

    private void ApplyFont()
    {
        if (Model?.TerminalFontFamily is { } family)
        {
            Terminal.FontFamily = new FontFamily(family);
        }
        else
        {
            Terminal.ClearValue(Octopus.Terminal.Avalonia.TerminalControl.FontFamilyProperty);
        }
    }

    public Task<bool> HasRunningChildAsync() => Terminal.HasRunningChildAsync();

    public Task CloseAsync() => Terminal.CloseAsync();

    public Task RestartAsync() => Terminal.RestartAsync();

    public void FocusContent() => Terminal.FocusTerminal();
}
