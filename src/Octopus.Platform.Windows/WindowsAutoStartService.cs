using System.Runtime.InteropServices;
using System.Text;

namespace Octopus.Platform.Windows;

/// <summary>
/// Manages per-user Windows auto-start via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (value name
/// <c>Octopus</c>). Uses Win32 <c>advapi32.dll</c> directly so it stays dependency-free and NativeAOT-compatible.
/// </summary>
public sealed class WindowsAutoStartService : IAutoStartService
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Octopus";

    private readonly string? _executablePath;
    private readonly IWindowsRunRegistry _registry;

    public WindowsAutoStartService()
        : this(executablePath: null, registry: null)
    {
    }

    internal WindowsAutoStartService(string? executablePath = null, IWindowsRunRegistry? registry = null)
    {
        var candidate = executablePath ?? Environment.ProcessPath;
        _executablePath = IsEligibleExecutable(candidate) ? candidate : null;
        _registry = registry ?? Win32RunRegistry.Instance;
    }

    public bool IsSupported => _executablePath is not null;

    internal string? ExecutablePath => _executablePath;

    public bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(_registry.GetValue(RunSubKey, ValueName));
    }

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported || _executablePath is null)
        {
            return false;
        }
        if (enabled)
        {
            var command = FormatCommand(_executablePath);
            if (string.Equals(_registry.GetValue(RunSubKey, ValueName), command, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return _registry.SetValue(RunSubKey, ValueName, command);
        }
        return _registry.DeleteValue(RunSubKey, ValueName);
    }

    internal static string FormatCommand(string executablePath) => $"\"{executablePath}\"";

    private static bool IsEligibleExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        var isRooted = Path.IsPathRooted(path)
            || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        if (!isRooted)
        {
            return false;
        }
        var fileName = ExtractFileName(path);
        var stem = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }
        return !stem.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("testhost", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("vstest.console", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractFileName(string path)
    {
        var idx = path.LastIndexOfAny(['\\', '/']);
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    internal interface IWindowsRunRegistry
    {
        string? GetValue(string subKey, string name);
        bool SetValue(string subKey, string name, string value);
        bool DeleteValue(string subKey, string name);
    }

    private sealed class Win32RunRegistry : IWindowsRunRegistry
    {
        private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));
        private const int KeyRead = 0x20019;
        private const int KeySetValue = 0x0002;
        private const uint RegSz = 1;
        private const uint RegExpandSz = 2;
        private const int ErrorSuccess = 0;
        private const int ErrorFileNotFound = 2;

        public static Win32RunRegistry Instance { get; } = new();

        public string? GetValue(string subKey, string name)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }
            if (RegOpenKeyEx(HkeyCurrentUser, subKey, 0, KeyRead, out var key) != ErrorSuccess)
            {
                return null;
            }
            try
            {
                uint size = 0;
                if (RegQueryValueEx(key, name, IntPtr.Zero, out var type, null, ref size) != ErrorSuccess
                    || (type != RegSz && type != RegExpandSz)
                    || size == 0)
                {
                    return null;
                }
                var buffer = new byte[size];
                if (RegQueryValueEx(key, name, IntPtr.Zero, out type, buffer, ref size) != ErrorSuccess
                    || (type != RegSz && type != RegExpandSz))
                {
                    return null;
                }
                return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
            }
            finally
            {
                _ = RegCloseKey(key);
            }
        }

        public bool SetValue(string subKey, string name, string value)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
            if (RegCreateKeyEx(HkeyCurrentUser, subKey, 0, null, 0, KeySetValue, IntPtr.Zero, out var key, out _) != ErrorSuccess)
            {
                return false;
            }
            try
            {
                var bytes = Encoding.Unicode.GetBytes(value + '\0');
                return RegSetValueEx(key, name, 0, RegSz, bytes, (uint)bytes.Length) == ErrorSuccess;
            }
            finally
            {
                _ = RegCloseKey(key);
            }
        }

        public bool DeleteValue(string subKey, string name)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
            var open = RegOpenKeyEx(HkeyCurrentUser, subKey, 0, KeySetValue, out var key);
            if (open == ErrorFileNotFound)
            {
                return true;
            }
            if (open != ErrorSuccess)
            {
                return false;
            }
            try
            {
                var deleted = RegDeleteValue(key, name);
                return deleted is ErrorSuccess or ErrorFileNotFound;
            }
            finally
            {
                _ = RegCloseKey(key);
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
        private static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
        private static extern int RegCreateKeyEx(
            IntPtr hKey,
            string lpSubKey,
            uint reserved,
            string? lpClass,
            uint dwOptions,
            int samDesired,
            IntPtr lpSecurityAttributes,
            out IntPtr phkResult,
            out uint lpdwDisposition);

        [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueEx(
            IntPtr hKey,
            string lpValueName,
            IntPtr lpReserved,
            out uint lpType,
            byte[]? lpData,
            ref uint lpcbData);

        [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
        private static extern int RegSetValueEx(
            IntPtr hKey,
            string lpValueName,
            uint reserved,
            uint dwType,
            byte[] lpData,
            uint cbData);

        [DllImport("advapi32.dll", EntryPoint = "RegDeleteValueW", CharSet = CharSet.Unicode)]
        private static extern int RegDeleteValue(IntPtr hKey, string lpValueName);

        [DllImport("advapi32.dll", EntryPoint = "RegCloseKey")]
        private static extern int RegCloseKey(IntPtr hKey);
    }
}
