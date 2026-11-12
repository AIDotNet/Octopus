using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Octopus.Platform.Updates;

/// <summary>What the three update installers share: the target name, write checks, scripts and detached launches.</summary>
public static class UpdateHelper
{
    /// <summary>
    /// <c>&lt;os&gt;-&lt;architecture of this process&gt;</c>. The process's, not the machine's: an x64 build under Rosetta
    /// keeps updating to the x64 build.
    /// </summary>
    public static string Target(string os) => os + "-" + RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>True when a file can be created in <paramref name="directory"/> (and removed again).</summary>
    public static bool CanWrite(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var probe = Path.Combine(directory, $".octopus-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Single-quotes for sh: the text reaches the program unchanged, whatever it holds.</summary>
    public static string ShQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>Single-quotes for PowerShell: only the quote characters themselves need doubling.</summary>
    public static string PsQuote(string text)
    {
        var builder = new StringBuilder("'");
        foreach (var c in text)
        {
            builder.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') builder.Append(c);
        }
        return builder.Append('\'').ToString();
    }

    /// <summary>Writes an sh script (LF line ends, executable).</summary>
    public static void WriteShellScript(string path, string content)
    {
        File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Writes a PowerShell script. With a BOM: Windows PowerShell reads a file without one in the ANSI code page.</summary>
    public static void WritePowerShellScript(string path, string content) =>
        File.WriteAllText(path, content.ReplaceLineEndings("\r\n"), new UTF8Encoding(true));

    /// <summary>
    /// Starts a helper that outlives the app: no window, no shared standard streams. Throws
    /// <see cref="UpdateInstallException"/> when it cannot be started.
    /// </summary>
    public static void StartDetached(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new UpdateInstallException($"{fileName} did not start.");
        }
        catch (Win32Exception e)
        {
            throw new UpdateInstallException($"{fileName} could not be started: {e.Message}", e);
        }
    }

    /// <summary>The part of an sh script that waits (up to two minutes) for the app's process to end.</summary>
    public static string ShWaitForProcess(int processId) =>
        $$"""
        i=0
        while kill -0 {{processId}} 2>/dev/null; do
          i=$((i + 1))
          [ "$i" -ge 600 ] && { echo "the app (pid {{processId}}) is still running; installing anyway"; break; }
          sleep 0.2
        done
        """;
}
