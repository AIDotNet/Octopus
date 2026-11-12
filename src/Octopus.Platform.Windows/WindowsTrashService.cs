using System.Runtime.InteropServices;

namespace Octopus.Platform.Windows;

/// <summary>
/// The Recycle Bin, through <c>SHFileOperationW(FO_DELETE)</c> with <c>FOF_ALLOWUNDO</c>. No dialogs: no confirmation,
/// no progress, no error UI (the caller reports errors). Not verified on real Windows.
/// </summary>
/// <remarks>
/// <c>IFileOperation</c> is the modern replacement, but it is COM, which under NativeAOT needs generated COM wrappers;
/// this flat call with a blittable struct needs nothing. One trap of <c>FOF_ALLOWUNDO</c>: an item that cannot be
/// recycled (network shares, removable drives, more than the bin may hold) is deleted permanently without a word. The
/// first two are refused up front (false, so the caller asks before deleting permanently); the last one cannot be
/// detected beforehand.
/// </remarks>
public sealed class WindowsTrashService : ITrashService
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>SHFileOperationW does not take long-path syntax: anything at or over MAX_PATH is not trashed.</summary>
    private const int MaxPath = 260;

    public bool MoveToTrash(string path)
    {
        // The struct below has the 64-bit layout (shellapi.h packs it to 1 byte only for 32-bit builds).
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("Nothing to move to the Recycle Bin.", full);
        if (full.Length >= MaxPath || !HasRecycleBin(full)) return false;

        // pFrom is a list of NUL-terminated names ending with an extra NUL: the explicit one plus the one
        // StringToHGlobalUni adds.
        var from = Marshal.StringToHGlobalUni(full + "\0");
        try
        {
            var operation = new ShFileOperation
            {
                Function = FoDelete,
                From = from,
                Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
            };
            var result = SHFileOperationW(ref operation);
            if (result != 0) throw new IOException($"Moving to the Recycle Bin failed (SHFileOperation error 0x{result:X}).");
            if (operation.AnyOperationsAborted != 0) throw new IOException("Moving to the Recycle Bin was aborted.");
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    /// <summary>Only fixed local drives have a Recycle Bin; UNC paths and removable drives do not.</summary>
    private static bool HasRecycleBin(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>SHFILEOPSTRUCTW. Pointers only, so it is blittable and needs no marshalling code (NativeAOT).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHFileOperationW(ref ShFileOperation operation);
}
