using System.IO.Compression;
using Octopus.Core.Plugins;
using Octopus.Plugins.Manifests;

namespace Octopus.Plugins.Packages;

/// <summary>A package unpacked into the staging area, waiting for the user to confirm (design/plugins/03 §5).</summary>
/// <param name="Root">The package directory inside the staging area (where plugin.json is).</param>
/// <param name="InstalledVersion">The version already installed under the same id; null for a new plugin.</param>
public sealed record StagedPlugin(string StagingDirectory, string Root, ManifestReadResult Result, string? InstalledVersion)
{
    public PluginManifest? Manifest => Result.Manifest;
    public bool CanInstall => Result.IsValid;
    public bool IsUpgrade => InstalledVersion is not null;
}

/// <summary>The package was refused before anything was installed.</summary>
public sealed class PluginPackageException(string message) : Exception(message);

/// <summary>
/// Unpacks <c>.octoplugin</c> files (zip) and package directories into <c>&lt;data&gt;/plugins/&lt;id&gt;</c>. A package may not
/// write outside its directory, contain links, exceed <see cref="MaxUnpackedBytes"/> or <see cref="MaxEntries"/> files.
/// </summary>
public sealed class PluginInstaller(PluginPaths paths)
{
    public const long MaxUnpackedBytes = 64L * 1024 * 1024;
    public const int MaxEntries = 5000;

    /// <summary>Unpacks a package file or copies a package directory into the staging area and reads its manifest.</summary>
    /// <exception cref="PluginPackageException">The package is unsafe or unreadable.</exception>
    public StagedPlugin Stage(string source)
    {
        var staging = Path.Combine(paths.StagingDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            if (Directory.Exists(source))
            {
                CopyDirectory(Path.GetFullPath(source), staging);
            }
            else if (File.Exists(source))
            {
                Extract(source, staging);
            }
            else
            {
                throw new PluginPackageException($"{source} does not exist");
            }
            var root = FindRoot(staging);
            var result = PluginManifestReader.ReadPackage(root, PluginSource.File, requireDirectoryName: false);
            string? installed = null;
            if (result.Manifest is { } manifest)
            {
                var existing = paths.PackageDirectoryOf(manifest.Id);
                if (Directory.Exists(existing))
                {
                    installed = PluginManifestReader.ReadPackage(existing, PluginSource.File, requireDirectoryName: false).Manifest?.Version ?? "?";
                }
            }
            return new StagedPlugin(staging, root, result, installed);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Moves a staged package into place, replacing an older version; returns the package directory.</summary>
    public string Install(StagedPlugin staged)
    {
        if (!staged.CanInstall || staged.Manifest is not { } manifest)
        {
            throw new InvalidOperationException("The staged package has errors and cannot be installed.");
        }
        var target = paths.PackageDirectoryOf(manifest.Id);
        Directory.CreateDirectory(paths.PluginsDirectory);
        string? backup = null;
        if (Directory.Exists(target))
        {
            backup = Path.Combine(paths.PluginsDirectory, $".backup-{manifest.Id}-{Guid.NewGuid():N}");
            Directory.Move(target, backup);
        }
        try
        {
            Directory.Move(staged.Root, target);
        }
        catch
        {
            if (backup is not null) Directory.Move(backup, target);
            throw;
        }
        if (backup is not null) TryDelete(backup);
        TryDelete(staged.StagingDirectory);
        return target;
    }

    public void Discard(StagedPlugin staged) => TryDelete(staged.StagingDirectory);

    /// <summary>Removes an installed package and, when asked, the plugin's own data directory.</summary>
    public void Uninstall(string id, bool removeData)
    {
        TryDelete(paths.PackageDirectoryOf(id));
        if (removeData) TryDelete(paths.DataDirectoryOf(id));
    }

    // ---------------------------------------------------------------- unpacking

    private static void Extract(string archive, string staging)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(archive);
        }
        catch (InvalidDataException)
        {
            throw new PluginPackageException("the file is not a plugin package (not a zip archive)");
        }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries)
            {
                throw new PluginPackageException($"the package has more than {MaxEntries} files");
            }
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (IsLink(entry))
                {
                    throw new PluginPackageException($"the package contains a link ({name}); links are not allowed");
                }
                var target = SafeTarget(staging, name);
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                // The sizes in the zip directory can lie: count what is actually written.
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    total += read;
                    if (total > MaxUnpackedBytes)
                    {
                        throw new PluginPackageException($"the package unpacks to more than {MaxUnpackedBytes / 1024 / 1024} MB");
                    }
                    output.Write(buffer, 0, read);
                }
            }
        }
    }

    /// <summary>Unix file type bits in the zip entry's external attributes: 0xA000 is a symbolic link.</summary>
    private static bool IsLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    /// <summary>Where an entry may be written; refuses absolute names, drive letters and climbing out with <c>..</c>.</summary>
    private static string SafeTarget(string staging, string name)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Contains('\0')
            || name.Split('/').Any(segment => segment == ".."))
        {
            throw new PluginPackageException($"the package contains an unsafe path ({name})");
        }
        var target = Path.GetFullPath(Path.Combine(staging, name));
        if (!PackagePath.IsInside(Path.GetFullPath(staging), target))
        {
            throw new PluginPackageException($"the package contains an unsafe path ({name})");
        }
        return target;
    }

    private static void CopyDirectory(string source, string staging)
    {
        long total = 0;
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(source, entry.FullName);
                if (entry.LinkTarget is not null)
                {
                    // Following it would copy whatever it points at (possibly a secret) into the package.
                    throw new PluginPackageException($"the directory contains a link ({relative}); links are not allowed");
                }
                if (++count > MaxEntries)
                {
                    throw new PluginPackageException($"the package has more than {MaxEntries} files");
                }
                var target = Path.Combine(staging, relative);
                if (entry is DirectoryInfo)
                {
                    Directory.CreateDirectory(target);
                    pending.Push(entry.FullName);
                    continue;
                }
                total += ((FileInfo)entry).Length;
                if (total > MaxUnpackedBytes)
                {
                    throw new PluginPackageException($"the package is larger than {MaxUnpackedBytes / 1024 / 1024} MB");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry.FullName, target);
            }
        }
    }

    /// <summary>plugin.json at the top, or in the only folder at the top (zipping a folder usually keeps the folder).</summary>
    private static string FindRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, PluginApi.ManifestFileName)))
        {
            return staging;
        }
        var entries = Directory.GetFileSystemEntries(staging);
        if (entries.Length == 1 && Directory.Exists(entries[0]) && File.Exists(Path.Combine(entries[0], PluginApi.ManifestFileName)))
        {
            return entries[0];
        }
        throw new PluginPackageException($"{PluginApi.ManifestFileName} is not at the top of the package");
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
