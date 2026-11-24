using System.Collections.Concurrent;
using Octopus.Platform;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Octopus.Storage;

namespace Octopus.App.Remote;

/// <summary>This computer's device key, shared by its two roles (a host for other devices, a device for other hosts). Made on first use.</summary>
public sealed class RemoteIdentityProvider(IAppPaths paths, ISecretStore secrets)
{
    private const string KeyService = "Octopus/remote";
    private readonly SemaphoreSlim _loading = new(1, 1);
    private DeviceIdentity? _identity;

    /// <summary>Where the paired devices and hosts are kept.</summary>
    public string Folder => Path.Combine(paths.DataDirectory, "remote");

    public async Task<DeviceIdentity> GetAsync()
    {
        if (_identity is { } ready) return ready;
        await _loading.WaitAsync().ConfigureAwait(false);
        try
        {
            return _identity ??= await DeviceIdentityStore.LoadOrCreateAsync(new KeyVault(secrets), Path.Combine(Folder, "device.key")).ConfigureAwait(false);
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>A secret of remote operation (beside the device key): from the keystore, or from a file only this user can read where there is none.</summary>
    public async Task<string?> LoadSecretAsync(string name)
    {
        try
        {
            if (secrets.IsAvailable) return await secrets.GetAsync(KeyService, name).ConfigureAwait(false);
            var file = Path.Combine(Folder, name);
            return File.Exists(file) ? (await File.ReadAllTextAsync(file).ConfigureAwait(false)).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task SaveSecretAsync(string name, string value)
    {
        if (secrets.IsAvailable)
        {
            await secrets.SetAsync(KeyService, name, value).ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(Folder);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(Path.Combine(Folder, name), options);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(value)).ConfigureAwait(false);
    }

    private sealed class KeyVault(ISecretStore secrets) : IKeyVault
    {
        public bool IsAvailable => secrets.IsAvailable;
        public Task<string?> GetAsync(string name) => secrets.GetAsync(KeyService, name);
        public Task SetAsync(string name, string value) => secrets.SetAsync(KeyService, name, value);
    }
}

/// <summary>
/// The device side of remote operation inside the desktop app (<see cref="RemoteHostConnections"/>, shared with the
/// phone app): this computer's key from the keystore, its paired hosts beside it, its name from the settings, and the
/// relay it is signed in to.
/// </summary>
public sealed class RemoteClientRuntime(RemoteIdentityProvider identity, ISettingsStore settings, RelayAccess? relay = null)
    : RemoteHostConnections(
        identity.GetAsync,
        () => new PairedPeerStore(Path.Combine(identity.Folder, "hosts.json")),
        () => new DeviceDescription(string.IsNullOrWhiteSpace(settings.Current.Remote.DeviceName) ? Environment.MachineName : settings.Current.Remote.DeviceName!.Trim(), DeviceKind.Desktop),
        RemoteHostRuntime.Hello,
        relay);
