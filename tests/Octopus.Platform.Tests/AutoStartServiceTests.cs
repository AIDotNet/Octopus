using Octopus.Platform.Linux;
using Octopus.Platform.MacOS;
using Octopus.Platform.Windows;

namespace Octopus.Platform.Tests;

public sealed class AutoStartServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("octopus-autostart-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ macOS

    [Fact]
    public void Mac_service_rejects_unbundled_translocated_and_dmg_paths()
    {
        var agentsDir = Path.Combine(_root, "mac-agents");
        var backend = new FakeMacLoginItemBackend();

        Assert.False(new MacAutoStartService(Path.Combine(_root, "bin", "Debug", "net10.0"), agentsDir, backend).IsSupported);
        Assert.False(new MacAutoStartService("/private/var/folders/x/AppTranslocation/abc/d/Octopus.app/Contents/MacOS", agentsDir, backend).IsSupported);
        Assert.False(new MacAutoStartService("/Volumes/Octopus/Octopus.app/Contents/MacOS", agentsDir, backend).IsSupported);
    }

    [Fact]
    public void Mac_service_uses_SMAppService_and_cleans_legacy_plist()
    {
        var agentsDir = Path.Combine(_root, "mac-sm-agents");
        var appDir = Path.Combine(_root, "Applications", "Octopus.app", "Contents", "MacOS");
        var backend = new FakeMacLoginItemBackend();
        var service = new MacAutoStartService(appDir, agentsDir, backend);

        Assert.True(service.IsSupported);
        Assert.False(service.IsEnabled());

        // Seed a legacy LaunchAgent plist to ensure SMAppService registration removes it (no double-launch).
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(service.PlistPath, "<plist/>");

        Assert.True(service.SetEnabled(true));
        Assert.True(service.IsEnabled());
        Assert.Equal(1, backend.RegisterCalls);
        Assert.False(File.Exists(service.PlistPath));

        // Idempotent second enable does not call register again.
        Assert.True(service.SetEnabled(true));
        Assert.Equal(1, backend.RegisterCalls);

        Assert.True(service.SetEnabled(false));
        Assert.False(service.IsEnabled());
        Assert.Equal(1, backend.UnregisterCalls);
    }

    [Fact]
    public void Mac_service_respects_user_revocation_in_system_settings()
    {
        var agentsDir = Path.Combine(_root, "mac-revoked-agents");
        var appDir = Path.Combine(_root, "Applications", "Octopus.app", "Contents", "MacOS");
        var backend = new FakeMacLoginItemBackend { Status = MacAutoStartService.MacLoginItemStatus.RequiresApproval };
        var service = new MacAutoStartService(appDir, agentsDir, backend);

        // Must NOT bypass user's System Settings denial by writing a LaunchAgent plist.
        Assert.False(service.SetEnabled(true));
        Assert.False(File.Exists(service.PlistPath));
        Assert.Equal(0, backend.RegisterCalls);

        // Disabling unregisters cleanly.
        Assert.True(service.SetEnabled(false));
        Assert.Equal(1, backend.UnregisterCalls);
    }

    [Fact]
    public void Mac_service_falls_back_to_launch_agent_plist_when_SMAppService_rejects_bundle()
    {
        var agentsDir = Path.Combine(_root, "mac-fallback-agents");
        var appDir = Path.Combine(_root, "Applications", "Octo & Pus.app", "Contents", "MacOS");
        var backend = new FakeMacLoginItemBackend { RegisterSucceeds = false };
        var service = new MacAutoStartService(appDir, agentsDir, backend);

        Assert.True(service.SetEnabled(true));
        Assert.True(service.IsEnabled());
        Assert.True(File.Exists(service.PlistPath));

        var plist = File.ReadAllText(service.PlistPath);
        Assert.Contains("<string>com.aidotnet.octopus</string>", plist);
        Assert.Contains("<string>/usr/bin/open</string>", plist);
        Assert.Contains("Octo &amp; Pus.app", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);

        Assert.True(service.SetEnabled(false));
        Assert.False(service.IsEnabled());
        Assert.False(File.Exists(service.PlistPath));
    }

    // ------------------------------------------------------------------ Windows

    [Fact]
    public void Windows_service_rejects_dotnet_and_relative_paths()
    {
        var reg = new FakeWindowsRunRegistry();
        Assert.False(new WindowsAutoStartService(@"C:\Program Files\dotnet\dotnet.exe", reg).IsSupported);
        Assert.False(new WindowsAutoStartService(@"C:\tools\testhost.exe", reg).IsSupported);
        Assert.False(new WindowsAutoStartService("Octopus.exe", reg).IsSupported);
    }

    [Fact]
    public void Windows_service_writes_reads_and_deletes_run_registry_value()
    {
        var reg = new FakeWindowsRunRegistry();
        var exe = @"C:\Users\me\AppData\Local\Programs\Octopus\Octopus.exe";
        var service = new WindowsAutoStartService(exe, reg);

        Assert.True(service.IsSupported);
        Assert.False(service.IsEnabled());

        Assert.True(service.SetEnabled(true));
        Assert.True(service.IsEnabled());
        Assert.Equal($"\"{exe}\"", reg.Values[(WindowsAutoStartService.RunSubKey, WindowsAutoStartService.ValueName)]);
        Assert.Equal(1, reg.SetCalls);

        // Idempotent when already matching.
        Assert.True(service.SetEnabled(true));
        Assert.Equal(1, reg.SetCalls);

        Assert.True(service.SetEnabled(false));
        Assert.False(service.IsEnabled());
        Assert.False(reg.Values.ContainsKey((WindowsAutoStartService.RunSubKey, WindowsAutoStartService.ValueName)));
    }

    // ------------------------------------------------------------------ Linux

    [Fact]
    public void Linux_service_rejects_dotnet_and_relative_paths()
    {
        var home = Path.Combine(_root, "linux-home-bad");
        Assert.False(new LinuxAutoStartService("/usr/bin/dotnet", home, xdgConfigHome: "").IsSupported);
        Assert.False(new LinuxAutoStartService("Octopus", home, xdgConfigHome: "").IsSupported);
    }

    [Fact]
    public void Linux_service_writes_xdg_autostart_desktop_entry_and_escapes_exec()
    {
        var home = Path.Combine(_root, "linux-home");
        var xdgConfig = Path.Combine(_root, "linux-xdg");
        var installDir = Directory.CreateDirectory(Path.Combine(_root, "opt", "Octopus $100%")).FullName;
        var exe = Path.Combine(installDir, "Octopus");
        var icon = Path.Combine(installDir, "octopus.png");
        File.WriteAllText(icon, "");

        var service = new LinuxAutoStartService(exe, home, xdgConfig);
        Assert.True(service.IsSupported);
        Assert.False(service.IsEnabled());

        Assert.True(service.SetEnabled(true));
        Assert.True(service.IsEnabled());
        Assert.Equal(Path.Combine(xdgConfig, "autostart", "octopus.desktop"), service.DesktopEntryPath);

        var content = File.ReadAllText(service.DesktopEntryPath);
        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Type=Application", content);
        Assert.Contains("Name=Octopus", content);
        Assert.Contains(@"Octopus \$100%%", content);
        Assert.Contains($"Icon={icon}", content);
        Assert.Contains("X-GNOME-Autostart-enabled=true", content);

        // If the desktop environment marks it disabled, IsEnabled reflects that.
        File.AppendAllText(service.DesktopEntryPath, "Hidden=true\n");
        Assert.False(service.IsEnabled());

        // Re-enabling rewrites the clean enabled entry.
        Assert.True(service.SetEnabled(true));
        Assert.True(service.IsEnabled());

        Assert.True(service.SetEnabled(false));
        Assert.False(service.IsEnabled());
        Assert.False(File.Exists(service.DesktopEntryPath));
    }

    private sealed class FakeMacLoginItemBackend : MacAutoStartService.IMacLoginItemBackend
    {
        public bool IsAvailable { get; set; } = true;
        public MacAutoStartService.MacLoginItemStatus Status { get; set; } = MacAutoStartService.MacLoginItemStatus.NotRegistered;
        public bool RegisterSucceeds { get; set; } = true;
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }

        public MacAutoStartService.MacLoginItemStatus GetStatus() => Status;

        public bool Register()
        {
            RegisterCalls++;
            if (!RegisterSucceeds)
            {
                return false;
            }
            Status = MacAutoStartService.MacLoginItemStatus.Enabled;
            return true;
        }

        public bool Unregister()
        {
            UnregisterCalls++;
            Status = MacAutoStartService.MacLoginItemStatus.NotRegistered;
            return true;
        }
    }

    private sealed class FakeWindowsRunRegistry : WindowsAutoStartService.IWindowsRunRegistry
    {
        public Dictionary<(string SubKey, string Name), string> Values { get; } = [];
        public int SetCalls { get; private set; }

        public string? GetValue(string subKey, string name) =>
            Values.TryGetValue((subKey, name), out var value) ? value : null;

        public bool SetValue(string subKey, string name, string value)
        {
            SetCalls++;
            Values[(subKey, name)] = value;
            return true;
        }

        public bool DeleteValue(string subKey, string name)
        {
            Values.Remove((subKey, name));
            return true;
        }
    }
}
