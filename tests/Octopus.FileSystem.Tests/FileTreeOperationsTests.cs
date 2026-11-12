using Octopus.Platform;

namespace Octopus.FileSystem.Tests;

public class FileTreeOperationsTests
{
    private sealed class RecordingTrash(bool accept) : ITrashService
    {
        public List<string> Moved { get; } = [];
        public bool MoveToTrash(string path)
        {
            Moved.Add(path);
            return accept;
        }
    }

    private static string NewRoot() => Directory.CreateTempSubdirectory("octopus-ops-").FullName;

    [Fact]
    public async Task A_new_file_and_a_new_folder_are_created_and_a_taken_name_is_refused()
    {
        var root = NewRoot();
        var tree = new FileTreeService();

        await tree.CreateFileAsync(Path.Combine(root, "note.txt"));
        await tree.CreateDirectoryAsync(Path.Combine(root, "docs"));

        Assert.True(File.Exists(Path.Combine(root, "note.txt")));
        Assert.True(Directory.Exists(Path.Combine(root, "docs")));
        await Assert.ThrowsAsync<IOException>(() => tree.CreateFileAsync(Path.Combine(root, "note.txt")));
        await Assert.ThrowsAsync<IOException>(() => tree.CreateDirectoryAsync(Path.Combine(root, "docs")));
    }

    [Fact]
    public async Task A_file_is_renamed_and_a_rename_onto_a_taken_name_is_refused()
    {
        var root = NewRoot();
        var tree = new FileTreeService();
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");

        await Assert.ThrowsAsync<IOException>(() => tree.RenameAsync(Path.Combine(root, "a.txt"), Path.Combine(root, "b.txt")));
        await tree.RenameAsync(Path.Combine(root, "a.txt"), Path.Combine(root, "c.txt"));

        Assert.False(File.Exists(Path.Combine(root, "a.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(root, "c.txt")));
    }

    [Fact]
    public async Task A_folder_with_its_content_is_deleted_for_good_when_there_is_no_trash()
    {
        var root = NewRoot();
        var folder = Path.Combine(root, "old");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");

        await new FileTreeService().DeleteAsync(folder, isDirectory: true);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Deleting_goes_to_the_trash_when_this_computer_has_one()
    {
        var root = NewRoot();
        var file = Path.Combine(root, "gone.txt");
        File.WriteAllText(file, "x");
        var trash = new RecordingTrash(accept: true);

        await new FileTreeService(trash).DeleteAsync(file, isDirectory: false);

        Assert.Equal([file], trash.Moved);
    }
}
