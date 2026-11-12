using System.Collections.Concurrent;

namespace Octopus.Platform;

/// <summary>
/// The system's keystore (macOS Keychain, Windows Credential Manager, the Secret Service on Linux): small secrets kept
/// outside Octopus's own files. A secret is named by a service (<c>Octopus/&lt;plugin id&gt;</c>) and a name inside it.
/// </summary>
public interface ISecretStore
{
    /// <summary>False when this machine has no keystore Octopus can use (then every call throws <see cref="NotSupportedException"/>).</summary>
    bool IsAvailable { get; }

    /// <summary>The secret; null when there is none.</summary>
    Task<string?> GetAsync(string service, string name, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the secret.</summary>
    Task SetAsync(string service, string name, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes the secret; nothing happens when there is none.</summary>
    Task DeleteAsync(string service, string name, CancellationToken cancellationToken = default);
}

/// <summary>Secrets in memory only: the stand-in for tests.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<(string, string), string> _secrets = new();

    public bool IsAvailable => true;

    public Task<string?> GetAsync(string service, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_secrets.TryGetValue((service, name), out var value) ? value : null);

    public Task SetAsync(string service, string name, string value, CancellationToken cancellationToken = default)
    {
        _secrets[(service, name)] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string service, string name, CancellationToken cancellationToken = default)
    {
        _secrets.TryRemove((service, name), out _);
        return Task.CompletedTask;
    }
}

/// <summary>No keystore: the stand-in for platforms without one.</summary>
public sealed class NoSecretStore : ISecretStore
{
    public bool IsAvailable => false;
    public Task<string?> GetAsync(string service, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException("There is no keystore on this machine.");
    public Task SetAsync(string service, string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException("There is no keystore on this machine.");
    public Task DeleteAsync(string service, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException("There is no keystore on this machine.");
}
