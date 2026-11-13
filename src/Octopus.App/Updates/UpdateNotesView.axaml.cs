using Avalonia.Controls;

namespace Octopus.App.Updates;

/// <summary>The body of the "what's new" dialog: the release notes, as Markdown.</summary>
public partial class UpdateNotesView : UserControl
{
    public UpdateNotesView() : this("")
    {
    }

    public UpdateNotesView(string markdown)
    {
        InitializeComponent();
        Notes.Text = markdown;
    }
}
