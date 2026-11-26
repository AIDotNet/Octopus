using Octopus.Mobile.Remote;
using Security;

namespace Octopus.Mobile.iOS;

/// <summary>
/// The phone's secrets (its device key, its sign-in) in the iOS keychain, readable only by this app and only on this
/// phone (ThisDeviceOnly: not in a backup, not moved to a new phone, which pairs again as a new device). Ordinary
/// files (the paired hosts) are in <see cref="DataDirectory"/>.
/// </summary>
public sealed class KeychainMobileStore : IMobileStore
{
    private const string Service = "com.aidotnet.octopus.mobile";

    public KeychainMobileStore(string directory)
    {
        DataDirectory = directory;
        Directory.CreateDirectory(directory);
    }

    public string DataDirectory { get; }

    private static SecRecord Query(string name) => new(SecKind.GenericPassword) { Service = Service, Account = name };

    public Task<string?> GetSecretAsync(string name)
    {
        var found = SecKeyChain.QueryAsData(Query(name), wantPersistentReference: false, out var status);
        if (status == SecStatusCode.ItemNotFound || found is null) return Task.FromResult<string?>(null);
        if (status != SecStatusCode.Success) throw new IOException($"The keychain did not give the secret {name}: {status}.");
        return Task.FromResult<string?>(found.ToString(NSStringEncoding.UTF8));
    }

    public Task SetSecretAsync(string name, string? value)
    {
        var removed = SecKeyChain.Remove(Query(name));
        if (removed is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
        {
            throw new IOException($"The keychain did not remove the secret {name}: {removed}.");
        }
        if (value is null) return Task.CompletedTask;
        var added = SecKeyChain.Add(new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = name,
            ValueData = NSData.FromString(value, NSStringEncoding.UTF8),
            Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
        });
        if (added != SecStatusCode.Success) throw new IOException($"The keychain did not keep the secret {name}: {added}.");
        return Task.CompletedTask;
    }
}
