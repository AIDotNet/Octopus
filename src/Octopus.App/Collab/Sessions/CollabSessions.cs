using System.Collections.Concurrent;
using Octopus.Core.Projects;

namespace Octopus.App.Collab.Sessions;

/// <summary>
/// Reaches one kind of session for the collaboration bus (a GUI tab here, a terminal here, a conversation on another
/// computer…) and hands it over as an <see cref="ICollabSessionBridge"/>, so the coordinator never branches on the kind.
/// To support a new kind of session: implement this and add it to <see cref="CollabSessions"/>' provider list.
/// </summary>
internal interface ICollabSessionProvider : IDisposable
{
    /// <summary>Whether this provider is the one for <paramref name="item"/> (first match wins).</summary>
    bool Handles(Project project, ProjectItem item);

    /// <summary>A bridge to the session if it already runs, without starting or opening anything; null otherwise.</summary>
    ICollabSessionBridge? TryAttach(Project project, ProjectItem item);

    /// <summary>A bridge to the session, starting or following it when needed; null when it cannot be reached.</summary>
    Task<ICollabSessionBridge?> EnsureAsync(Project project, ProjectItem item, CancellationToken ct);
}

/// <summary>
/// All sessions the collaboration bus talks to, whatever their kind: the bridges GUI tabs register themselves, and the
/// ones providers create on demand (terminals, remote conversations).
/// </summary>
internal sealed class CollabSessions : IDisposable
{
    private readonly ConcurrentDictionary<string, ICollabSessionBridge> _bridges = new(StringComparer.Ordinal);
    // Bridges providers created (released here); registered GUI tabs belong to their tab and are never disposed here.
    private readonly ConcurrentDictionary<ICollabSessionBridge, byte> _owned = new();
    private readonly List<ICollabSessionProvider> _providers = [];

    public void AddProvider(ICollabSessionProvider provider) => _providers.Add(provider);

    /// <summary>The bridge already known for <paramref name="itemId"/> (registered or created earlier).</summary>
    public ICollabSessionBridge? Find(string itemId) => _bridges.GetValueOrDefault(itemId);

    public bool Contains(string itemId) => _bridges.ContainsKey(itemId);

    /// <summary>A GUI tab announces itself (it replaces any earlier bridge for the item).</summary>
    public void Register(ICollabSessionBridge bridge) => _bridges[bridge.ItemId] = bridge;

    /// <summary>Forgets <paramref name="itemId"/>'s bridge; false when there was none. Does not dispose a registered tab.</summary>
    public bool Unregister(string itemId) => _bridges.TryRemove(itemId, out _);

    /// <summary>Forgets <paramref name="bridge"/> only if it is still the one known for its item.</summary>
    public bool Unregister(ICollabSessionBridge bridge)
    {
        if (!_bridges.TryRemove(new KeyValuePair<string, ICollabSessionBridge>(bridge.ItemId, bridge))) return false;
        Release(bridge);
        return true;
    }

    private void Release(ICollabSessionBridge bridge)
    {
        if (_owned.TryRemove(bridge, out _) && bridge is IDisposable owned) owned.Dispose();
    }

    /// <summary>Adds a bridge a provider created, unless another one won the race (then it is released); returns the one in use.</summary>
    public ICollabSessionBridge Adopt(ICollabSessionBridge bridge)
    {
        var inUse = _bridges.GetOrAdd(bridge.ItemId, bridge);
        if (ReferenceEquals(inUse, bridge)) _owned.TryAdd(bridge, 0);
        else if (!_owned.ContainsKey(bridge) && bridge is IDisposable loser) loser.Dispose();
        return inUse;
    }

    /// <summary>The item is gone: forget its bridge and release it when it was created here.</summary>
    public void Remove(string itemId)
    {
        if (_bridges.TryRemove(itemId, out var bridge)) Release(bridge);
    }

    public ICollabSessionBridge? TryAttach(Project project, ProjectItem item)
    {
        if (Find(item.Id) is { } known) return known;
        return ProviderFor(project, item)?.TryAttach(project, item) is { } attached ? Adopt(attached) : null;
    }

    public async Task<ICollabSessionBridge?> EnsureAsync(Project project, ProjectItem item, CancellationToken ct)
    {
        if (Find(item.Id) is { } known) return known;
        if (ProviderFor(project, item) is not { } provider) return null;
        // A GUI tab registers itself while starting, so the provider's answer may already be the registered bridge.
        var bridge = await provider.EnsureAsync(project, item, ct).ConfigureAwait(false);
        return bridge is null ? Find(item.Id) : Find(item.Id) ?? Adopt(bridge);
    }

    private ICollabSessionProvider? ProviderFor(Project project, ProjectItem item) => _providers.FirstOrDefault(p => p.Handles(project, item));

    public void Dispose()
    {
        foreach (var bridge in _owned.Keys)
        {
            if (bridge is IDisposable owned) owned.Dispose();
        }
        _owned.Clear();
        _bridges.Clear();
        foreach (var provider in _providers) provider.Dispose();
    }
}
