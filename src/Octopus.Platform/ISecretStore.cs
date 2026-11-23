using System.Collections.Concurrent;

namespace Octopus.Platform;

/// <summary>
/// Names a third-party CLI's OS keyring slot (such as <c>go-keyring</c> used by Antigravity CLI), whose naming on
/// Windows (<c>service:account</c>) and Linux (<c>username</c>) differs from Octopus's own <c>service/name</c> entries.
/// </summary>
public sealed record ExternalKeyringSlot(
    string Service,
    string Account,
    string? WindowsTarget = null,
    string LinuxUserAttribute = "username")
{
    public string EffectiveWindowsTarget => string.IsNullOrWhiteSpace(WindowsTarget) ? $"{Service}:{Account}" : WindowsTarget!;
}

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

    /// <summary>Reads a third-party CLI's OS keyring slot; defaults to <see cref="GetAsync"/>.</summary>
    Task<string?> GetExternalAsync(ExternalKeyringSlot slot, CancellationToken cancellationToken = default) =>
        GetAsync(slot.Service, slot.Account, cancellationToken);

    /// <summary>Writes a third-party CLI's OS keyring slot; defaults to <see cref="SetAsync"/>.</summary>
    Task SetExternalAsync(ExternalKeyringSlot slot, string value, CancellationToken cancellationToken = default) =>
        SetAsync(slot.Service, slot.Account, value, cancellationToken);

    /// <summary>Deletes a third-party CLI's OS keyring slot; defaults to <see cref="DeleteAsync"/>.</summary>
    Task DeleteExternalAsync(ExternalKeyringSlot slot, CancellationToken cancellationToken = default) =>
        DeleteAsync(slot.Service, slot.Account, cancellationToken);
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
