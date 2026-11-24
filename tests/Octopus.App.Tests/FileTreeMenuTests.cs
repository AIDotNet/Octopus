using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Explorer.Files;
using Octopus.App.Services;
using Octopus.FileSystem;
using Octopus.Platform;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>The file operations of the context menu, on this computer's folder.</summary>
public class FileTreeMenuTests
{
    private static (FileTreeViewModel Tree, FakeDialogService Dialogs, string Root) Local()
    {
        var root = Directory.CreateTempSubdirectory("octopus-menu-").FullName;
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        var dialogs = (FakeDialogService)AppHost.Get<IDialogService>();
        dialogs.NameAnswer = null;
        dialogs.Answer = true;
        var tree = new FileTreeViewModel(new FileTreeService(), AppHost.Get<IFileOpener>(), new PathComparer(ignoreCase: false), dialogs);
        tree.Load(root);
        return (tree, dialogs, root);
    }

    [AvaloniaFact]
    public async Task A_new_file_in_the_folder_of_a_row_is_created_next_to_it()
    {
        var (tree, dialogs, root) = Local();
        tree.ContextNode = tree.Roots.Single(n => n.Name == "a.txt");
        dialogs.NameAnswer = "b.txt";

        await tree.NewFileHereCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(root, "b.txt")));
        Assert.Contains("b.txt", tree.Roots.Select(n => n.Name));
    }

    [AvaloniaFact]
    public async Task A_new_folder_with_no_row_selected_goes_into_the_project_folder()
    {
        var (tree, dialogs, root) = Local();
        tree.ContextNode = null;
        dialogs.NameAnswer = "docs";

        await tree.NewFolderHereCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(Path.Combine(root, "docs")));
    }

    [AvaloniaFact]
    public async Task A_name_with_a_slash_is_refused_and_nothing_is_made()
    {
        var (tree, dialogs, root) = Local();
        tree.ContextNode = null;
        dialogs.NameAnswer = "x/y";

        await tree.NewFileHereCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(Path.Combine(root, "x")));
        Assert.Equal(1, dialogs.Asked);
    }

    [AvaloniaFact]
    public async Task A_row_is_renamed_and_a_taken_name_is_said_not_done()
    {
        var (tree, dialogs, root) = Local();
        File.WriteAllText(Path.Combine(root, "taken.txt"), "t");
        tree.ContextNode = tree.Roots.Single(n => n.Name == "a.txt");

        dialogs.NameAnswer = "taken.txt";
        await tree.RenameContextCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(root, "a.txt")));

        dialogs.NameAnswer = "c.txt";
        await tree.RenameContextCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(root, "c.txt")));
        Assert.False(File.Exists(Path.Combine(root, "a.txt")));
    }

    [AvaloniaFact]
    public async Task A_row_is_deleted_only_after_the_person_confirms()
    {
        var (tree, dialogs, root) = Local();
        tree.ContextNode = tree.Roots.Single(n => n.Name == "a.txt");

        dialogs.Answer = false;
        await tree.DeleteContextCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(root, "a.txt")));

        dialogs.Answer = true;
        await tree.DeleteContextCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(root, "a.txt")));
    }

    [AvaloniaFact]
    public void Rename_and_delete_need_a_row_and_creation_needs_a_folder()
    {
        var (tree, _, _) = Local();
        Assert.False(tree.RenameContextCommand.CanExecute(null));
        Assert.False(tree.DeleteContextCommand.CanExecute(null));
        Assert.True(tree.NewFileHereCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ContextNode_change_notifies_rename_and_delete_commands_and_root_change_notifies_creation()
    {
        var dialogs = (FakeDialogService)AppHost.Get<IDialogService>();
        var tree = new FileTreeViewModel(new FileTreeService(), AppHost.Get<IFileOpener>(), new PathComparer(ignoreCase: false), dialogs);
        var createChanges = 0;
        var renameChanges = 0;
        var deleteChanges = 0;
        tree.NewFileHereCommand.CanExecuteChanged += (_, _) => createChanges++;
        tree.RenameContextCommand.CanExecuteChanged += (_, _) => renameChanges++;
        tree.DeleteContextCommand.CanExecuteChanged += (_, _) => deleteChanges++;

        Assert.False(tree.NewFileHereCommand.CanExecute(null));
        var root = Directory.CreateTempSubdirectory("octopus-menu-notify-").FullName;
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");

        tree.Load(root);
        Assert.True(createChanges > 0);
        Assert.True(tree.NewFileHereCommand.CanExecute(null));

        renameChanges = 0;
        deleteChanges = 0;
        tree.ContextNode = tree.Roots.Single(n => n.Name == "a.txt");
        Assert.Equal(1, renameChanges);
        Assert.Equal(1, deleteChanges);
        Assert.True(tree.RenameContextCommand.CanExecute(null));
        Assert.True(tree.DeleteContextCommand.CanExecute(null));

        tree.ContextNode = null;
        Assert.Equal(2, renameChanges);
        Assert.Equal(2, deleteChanges);
        Assert.False(tree.RenameContextCommand.CanExecute(null));
        Assert.False(tree.DeleteContextCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Right_click_directly_on_unselected_row_or_its_blank_area_enables_rename_and_delete_menu_entries()
    {
        var (tree, _, _) = Local();
        var view = new FileTreeView { DataContext = tree };
        var window = new Window { Width = 320, Height = 300, Content = view };
        window.Show();
        Frames.Pump();

        var arcTree = view.GetVisualDescendants().OfType<TKCode.Arc.TreeView>().Single();
        var menu = TKCode.Arc.ContextMenu.GetMenu(arcTree);
        Assert.NotNull(menu);
        var entries = menu!.Items.OfType<MenuEntry>().ToList();
        var renameEntry = entries[^2];
        var deleteEntry = entries[^1];

        // Before any click, nothing is selected and Rename/Delete are disabled.
        Assert.Null(tree.SelectedNode);
        Assert.Null(tree.ContextNode);

        // Find the realized row Grid and simulate ContextRequested directly on the row's blank area (no prior left-click).
        var rowGrid = arcTree.GetVisualDescendants().OfType<Grid>().First(g => g.DataContext is FileNodeViewModel);
        Assert.Equal(Avalonia.Media.Brushes.Transparent, rowGrid.Background);

        // Also verify hitting the outer TreeViewRow directly (indent / chevron zone) resolves the row!
        var outerRow = rowGrid.GetVisualAncestors().OfType<Control>().First(c => c.GetType().Name == "TreeViewRow");
        outerRow.RaiseEvent(new ContextRequestedEventArgs());
        Frames.Pump();

        // Keyboard ContextRequested without position falls back to SelectedNode (still null here), then pointer-based sets it.
        var pointArgs = new ContextRequestedEventArgs(new PointerReleasedEventArgs(
            outerRow,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true),
            arcTree,
            new Point(4, 10),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased),
            KeyModifiers.None,
            MouseButton.Right));
        outerRow.RaiseEvent(pointArgs);
        Frames.Pump();

        var expected = tree.Roots.Single(n => n.Name == "a.txt");
        Assert.Same(expected, tree.ContextNode);
        Assert.Same(expected, tree.SelectedNode);
        Assert.True(renameEntry.IsEffectivelyEnabled);
        Assert.True(deleteEntry.IsEffectivelyEnabled);

        menu.Close(returnFocus: false);
        window.Close();
    }
}
