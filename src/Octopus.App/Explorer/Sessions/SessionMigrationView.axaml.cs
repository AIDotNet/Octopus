using Avalonia.Controls;

namespace Octopus.App.Explorer.Sessions;

/// <summary>The content of the migration dialog (design/ui/会话管理方案.html, the dialog shared by every layout).</summary>
public partial class SessionMigrationView : UserControl
{
    public SessionMigrationView()
    {
        InitializeComponent();
    }

    public SessionMigrationView(SessionMigrationFormViewModel model) : this()
    {
        DataContext = model;
    }
}
