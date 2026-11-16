using System.Diagnostics;
using System.Text;

namespace Octopus.Platform;

/// <summary>Builds an argv launch, using the Windows command interpreter only for batch shims.</summary>
public static class ProcessCommand
{
    public static ProcessStartInfo Create(string fileName, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(fileName);
        if (!OperatingSystem.IsWindows()
            || !(fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            return start;
        }

        // ArgumentList uses CRT quoting, not cmd's grammar. Keep the outer /s /c quotes verbatim.
        start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var command = new StringBuilder("/d /s /e:on /v:off /c \"");
        AppendBatchArgument(command, fileName);
        foreach (var argument in arguments)
        {
            command.Append(' ');
            AppendBatchArgument(command, argument);
        }
        command.Append('"');
        start.Arguments = command.ToString();
        return start;
    }

    internal static string QuoteBatchArgument(string value)
    {
        var command = new StringBuilder();
        AppendBatchArgument(command, value);
        return command.ToString();
    }

    private static void AppendBatchArgument(StringBuilder command, string value)
    {
        if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            throw new ArgumentException("Batch arguments cannot contain newlines or NUL.", nameof(value));

        command.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else
            {
                // Double embedded quotes for cmd, and preceding backslashes for a shim forwarding %*.
                if (c == '"') command.Append('\\', backslashes).Append('"');
                // Insert an empty substring expansion so %NAME% cannot expand at the command boundary.
                else if (c == '%') command.Append("%%cd:~,");
                backslashes = 0;
            }
            command.Append(c);
        }
        command.Append('\\', backslashes).Append('"');
    }
}
