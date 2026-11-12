using Octopus.App.Explorer.Files;
using Octopus.App.Services;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.FileSystem;
using Octopus.Platform;
using Xunit;
using Avalonia.Headless.XUnit;

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
}
