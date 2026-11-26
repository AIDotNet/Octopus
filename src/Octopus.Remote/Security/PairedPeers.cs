using System.Text.Json;
using System.Text.Json.Serialization;

namespace Octopus.Remote.Security;

/// <summary>A device this one is paired with: its key, what it called itself, and for a host where it was last reached.</summary>
public sealed class PairedPeer
{
    /// <summary>The device id (derived from <see cref="PublicKey"/>).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; } = DeviceKind.Desktop;
    /// <summary>The 65-byte public key, base64.</summary>
    public string PublicKey { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>A host's addresses (<c>ws://host:port</c>), the one that last worked first. Empty for a device that connects to this one.</summary>
    public List<string> Addresses { get; set; } = [];
    /// <summary>How a device was let in on a host's list: <see cref="PairedVia.Password"/>, or null for a person comparing numbers or a pairing link.</summary>
    public string? Via { get; set; }

    /// <summary>The key, or null when what is stored is not one.</summary>
    public DevicePublicKey? Key()
    {
        try { return DevicePublicKey.Parse(Convert.FromBase64String(PublicKey)); }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException) { return null; }
    }
}

/// <summary>Values of <see cref="PairedPeer.Via"/>.</summary>
public static class PairedVia
{
    /// <summary>With the host's access password, by a device of the same account.</summary>
    public const string Password = "password";
}

internal sealed class PairedPeersDocument
{
    public int Version { get; set; } = 1;
    public List<PairedPeer> Peers { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PairedPeersDocument))]
internal sealed partial class PairedPeersJsonContext : JsonSerializerContext;

/// <summary>
/// The paired devices of one role, in one JSON file: a host's list of devices that may connect
/// (<c>remote/devices.json</c>), or a device's list of hosts it knows (<c>remote/hosts.json</c>). Only public keys are
/// in it. Safe to use from any thread; every change is written at once.
/// </summary>
public sealed class PairedPeerStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly List<PairedPeer> _peers;

    /// <param name="path">The file; null keeps the list in memory only (tests).</param>
    public PairedPeerStore(string? path)
    {
        _path = path;
        _peers = Load(path);
    }

    /// <summary>The list changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<PairedPeer> Peers
    {
        get { lock (_gate) return [.. _peers]; }
    }

    public PairedPeer? Find(DevicePublicKey key)
    {
        var id = key.DeviceId;
        lock (_gate) return _peers.FirstOrDefault(peer => peer.Id == id && peer.Key() is { } stored && stored.Equals(key));
    }

    public PairedPeer? Find(string id)
    {
        lock (_gate) return _peers.FirstOrDefault(peer => peer.Id == id);
    }

    /// <summary>Remembers a device (or replaces what was known about it) and returns its entry.</summary>
    public PairedPeer Add(DevicePublicKey key, DeviceDescription description, IEnumerable<string>? addresses = null, string? via = null)
    {
        var peer = new PairedPeer
        {
            Id = key.DeviceId,
            Name = description.Name,
            Kind = description.Kind,
            PublicKey = Convert.ToBase64String(key.Bytes),
            PairedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            Addresses = addresses?.ToList() ?? [],
            Via = via,
        };
        lock (_gate)
        {
            _peers.RemoveAll(existing => existing.Id == peer.Id);
            _peers.Add(peer);
            Save();
        }
        Changed?.Invoke();
        return peer;
    }

    /// <summary>Forgets a device; false when it was not known.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_peers.RemoveAll(peer => peer.Id == id) == 0) return false;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Forgets every device that was let in a certain way; returns their ids.</summary>
    public IReadOnlyList<string> RemoveAll(string via)
    {
        List<string> removed;
        lock (_gate)
        {
            removed = [.. _peers.Where(peer => peer.Via == via).Select(peer => peer.Id)];
            if (removed.Count == 0) return removed;
            _peers.RemoveAll(peer => peer.Via == via);
            Save();
        }
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Notes that a device was just talked to: when, under which name, and for a host at which address.</summary>
    public void Touch(string id, string? name = null, string? address = null)
    {
        lock (_gate)
        {
            if (_peers.FirstOrDefault(peer => peer.Id == id) is not { } peer) return;
            peer.LastSeenAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(name)) peer.Name = name;
            if (!string.IsNullOrEmpty(address))
            {
                peer.Addresses.Remove(address);
                peer.Addresses.Insert(0, address);
            }
            Save();
        }
        Changed?.Invoke();
    }

    private static List<PairedPeer> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, PairedPeersJsonContext.Default.PairedPeersDocument)?.Peers ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read pairs with nobody: the person pairs again, nothing is let in by mistake.
            return [];
        }
    }

    // Written beside the file and moved over it: a crash leaves the old list or the new one, never half of one.
    private void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new PairedPeersDocument { Peers = _peers }, PairedPeersJsonContext.Default.PairedPeersDocument));
        File.Move(temporary, _path, overwrite: true);
    }
}

/// <summary>Where the device's private key is kept: the system's keystore, behind what the app's platform layer offers.</summary>
public interface IKeyVault
{
    /// <summary>False where there is no keystore (the key then goes to a file only this user can read).</summary>
    bool IsAvailable { get; }
    Task<string?> GetAsync(string name);
    Task SetAsync(string name, string value);
}

/// <summary>Loads this installation's device identity, making it the first time.</summary>
public static class DeviceIdentityStore
{
    public const string KeyName = "device-key";

    /// <param name="vault">The keystore; null or unavailable falls back to <paramref name="fallbackFile"/>.</param>
    /// <param name="fallbackFile">A file readable by this user only (created with those rights on Unix).</param>
    public static async Task<DeviceIdentity> LoadOrCreateAsync(IKeyVault? vault, string fallbackFile)
    {
        var useVault = vault is { IsAvailable: true };
        string? stored = null;
        try
        {
            stored = useVault ? await vault!.GetAsync(KeyName).ConfigureAwait(false) : File.Exists(fallbackFile) ? File.ReadAllText(fallbackFile) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try { return DeviceIdentity.Import(Convert.FromBase64String(stored.Trim())); }
            catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException) { /* unreadable: a new one below */ }
        }
        var identity = DeviceIdentity.Create();
        var exported = Convert.ToBase64String(identity.Export());
        if (useVault)
        {
            await vault!.SetAsync(KeyName, exported).ConfigureAwait(false);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fallbackFile)!);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(fallbackFile, options);
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(exported)).ConfigureAwait(false);
        }
        return identity;
    }
}
