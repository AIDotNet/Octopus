using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Octopus.Platform.MacOS;

/// <summary>
/// Manages macOS launch-at-login for a packaged <c>Octopus.app</c> bundle. Uses Apple's macOS 13+
/// <c>SMAppService.mainAppService</c> API (so Octopus appears under System Settings -> Login Items -> Open at Login),
/// and falls back to a per-user LaunchAgent plist (<c>~/Library/LaunchAgents/com.aidotnet.octopus.plist</c>) if
/// <c>SMAppService</c> rejects an unsigned/local bundle. Unbundled (<c>dotnet run</c>), AppTranslocation, and mounted
/// DMG (<c>/Volumes/...</c>) runs report <see cref="IsSupported"/> = false.
/// </summary>
public sealed class MacAutoStartService : IAutoStartService
{
    public const string LaunchAgentLabel = "com.aidotnet.octopus";
    public const string LaunchAgentFileName = LaunchAgentLabel + ".plist";

    private readonly string? _bundlePath;
    private readonly string _plistPath;
    private readonly IMacLoginItemBackend _loginItem;

    public MacAutoStartService()
        : this(appDirectory: null, launchAgentsDirectory: null, loginItem: null)
    {
    }

    internal MacAutoStartService(
        string? appDirectory = null,
        string? launchAgentsDirectory = null,
        IMacLoginItemBackend? loginItem = null)
    {
        var baseDir = Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory);
        var bundle = MacUpdateInstaller.BundleOf(baseDir);
        _bundlePath = IsEligibleBundle(bundle) ? bundle : null;

        var agentsDir = launchAgentsDirectory;
        if (string.IsNullOrWhiteSpace(agentsDir))
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            agentsDir = Path.Combine(home, "Library", "LaunchAgents");
        }
        _plistPath = Path.Combine(agentsDir, LaunchAgentFileName);
        _loginItem = loginItem ?? SmAppServiceBackend.Instance;
    }

    public bool IsSupported => _bundlePath is not null;

    internal string? BundlePath => _bundlePath;

    internal string PlistPath => _plistPath;

    public bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }
        if (_loginItem.IsAvailable && _loginItem.GetStatus() == MacLoginItemStatus.Enabled)
        {
            return true;
        }
        return File.Exists(_plistPath);
    }

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported || _bundlePath is null)
        {
            return false;
        }
        return enabled ? Enable(_bundlePath) : Disable();
    }

    private bool Enable(string bundlePath)
    {
        if (_loginItem.IsAvailable)
        {
            var status = _loginItem.GetStatus();
            if (status == MacLoginItemStatus.Enabled)
            {
                TryDeletePlist();
                return true;
            }
            if (status == MacLoginItemStatus.RequiresApproval)
            {
                // The user explicitly revoked Login Item permission in System Settings; respect that choice and do not
                // bypass it with a LaunchAgent plist.
                TryDeletePlist();
                return false;
            }
            if (_loginItem.Register() || _loginItem.GetStatus() == MacLoginItemStatus.Enabled)
            {
                TryDeletePlist();
                return true;
            }
            if (_loginItem.GetStatus() == MacLoginItemStatus.RequiresApproval)
            {
                TryDeletePlist();
                return false;
            }
        }

        return TryWritePlist(bundlePath);
    }

    private bool Disable()
    {
        var smOk = true;
        if (_loginItem.IsAvailable)
        {
            var status = _loginItem.GetStatus();
            if (status is MacLoginItemStatus.Enabled or MacLoginItemStatus.RequiresApproval)
            {
                smOk = _loginItem.Unregister()
                    || _loginItem.GetStatus() is MacLoginItemStatus.NotRegistered or MacLoginItemStatus.NotFound;
            }
        }

        var plistOk = TryDeletePlist();
        return smOk && plistOk;
    }

    internal static string BuildLaunchAgentPlist(string bundlePath)
    {
        var escapedBundle = SecurityElement.Escape(bundlePath) ?? bundlePath;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{LaunchAgentLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>/usr/bin/open</string>
                    <string>-a</string>
                    <string>{escapedBundle}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>ProcessType</key>
                <string>Interactive</string>
            </dict>
            </plist>
            """;
    }

    private static bool IsEligibleBundle(string? bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            return false;
        }
        if (bundlePath.Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            return false;
        }
        if (bundlePath.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private bool TryWritePlist(string bundlePath)
    {
        try
        {
            var expected = BuildLaunchAgentPlist(bundlePath);
            if (File.Exists(_plistPath) && string.Equals(File.ReadAllText(_plistPath, Encoding.UTF8), expected, StringComparison.Ordinal))
            {
                return true;
            }
            var dir = Path.GetDirectoryName(_plistPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var temp = _plistPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, expected, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, _plistPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryDeletePlist()
    {
        try
        {
            if (File.Exists(_plistPath))
            {
                File.Delete(_plistPath);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal enum MacLoginItemStatus
    {
        NotRegistered = 0,
        Enabled = 1,
        RequiresApproval = 2,
        NotFound = 3,
    }

    internal interface IMacLoginItemBackend
    {
        bool IsAvailable { get; }
        MacLoginItemStatus GetStatus();
        bool Register();
        bool Unregister();
    }

    /// <summary>Objective-C bridge to <c>SMAppService.mainAppService</c> in ServiceManagement.framework (macOS 13+).</summary>
    private sealed class SmAppServiceBackend : IMacLoginItemBackend
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string ServiceManagementFramework = "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement";

        public static SmAppServiceBackend Instance { get; } = new();

        private readonly IntPtr _mainAppService;

        private SmAppServiceBackend()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }
            try
            {
                NativeLibrary.TryLoad(ServiceManagementFramework, out _);
                var cls = GetClass("SMAppService");
                if (cls == IntPtr.Zero)
                {
                    return;
                }
                _mainAppService = SendIntPtr(cls, Sel("mainAppService"));
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                _mainAppService = IntPtr.Zero;
            }
        }

        public bool IsAvailable => _mainAppService != IntPtr.Zero;

        public MacLoginItemStatus GetStatus()
        {
            if (_mainAppService == IntPtr.Zero)
            {
                return MacLoginItemStatus.NotFound;
            }
            try
            {
                var status = (int)SendNInt(_mainAppService, Sel("status"));
                return Enum.IsDefined(typeof(MacLoginItemStatus), status)
                    ? (MacLoginItemStatus)status
                    : MacLoginItemStatus.NotFound;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return MacLoginItemStatus.NotFound;
            }
        }

        public bool Register()
        {
            if (_mainAppService == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return SendBoolWithError(_mainAppService, Sel("registerAndReturnError:"), IntPtr.Zero) != 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        public bool Unregister()
        {
            if (_mainAppService == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return SendBoolWithError(_mainAppService, Sel("unregisterAndReturnError:"), IntPtr.Zero) != 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        [SuppressMessage("Globalization", "CA2101", Justification = "Unix C API taking UTF-8; marshalled explicitly as LPUTF8Str.")]
        [DllImport(ObjC, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [SuppressMessage("Globalization", "CA2101", Justification = "Unix C API taking UTF-8; marshalled explicitly as LPUTF8Str.")]
        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern nint SendNInt(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern byte SendBoolWithError(IntPtr receiver, IntPtr selector, IntPtr errorOut);
    }
}
