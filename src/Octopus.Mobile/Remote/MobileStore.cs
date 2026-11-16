using Octopus.Remote.Relay;
using Octopus.Remote.Security;

namespace Octopus.Mobile.Remote;

/// <summary>
/// Where the phone keeps what must survive a restart: a folder for ordinary files (the paired hosts, the service's
/// address) and a place for secrets (this phone's key, its sign-in). A platform head gives the real one, on the
/// system's keystore; <see cref="FileMobileStore"/> serves the desktop preview and the tests.
/// </summary>
public interface IMobileStore
{
    string DataDirectory { get; }
    Task<string?> GetSecretAsync(string name);
    /// <summary>Keeps the secret; null removes it.</summary>
    Task SetSecretAsync(string name, string? value);
}

/// <summary>Everything in one folder; the secrets as files only this user can read. Not for a phone: there the keystore is used.</summary>
public sealed class FileMobileStore(string directory) : IMobileStore
{
    public string DataDirectory { get; } = directory;

    private string PathOf(string name) => Path.Combine(DataDirectory, "secrets", name);

    public async Task<string?> GetSecretAsync(string name) =>
        File.Exists(PathOf(name)) ? await File.ReadAllTextAsync(PathOf(name)).ConfigureAwait(false) : null;

    public async Task SetSecretAsync(string name, string? value)
    {
        var path = PathOf(name);
        if (value is null)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(value)).ConfigureAwait(false);
    }
}

/// <summary>The store as the shared remote code asks for it: the device key and the sign-in are two of its secrets.</summary>
internal sealed class MobileVault(IMobileStore store) : IKeyVault, IRelaySessionStore
{
    private const string SessionName = "relay-session";

    public bool IsAvailable => true;
    public Task<string?> GetAsync(string name) => store.GetSecretAsync(name);
    public Task SetAsync(string name, string value) => store.SetSecretAsync(name, value);
    public Task<string?> LoadAsync() => store.GetSecretAsync(SessionName);
    public Task SaveAsync(string? value) => store.SetSecretAsync(SessionName, value);
}
