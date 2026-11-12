namespace Octopus.FileSystem.Tests;

/// <summary>A temporary folder that is deleted with the test.</summary>
internal sealed class TempTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("octopus-fs-").FullName;

    public string File(string relative, string content = "")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
