using System.Runtime.ExceptionServices;
using Octopus.Core.Terminals;
using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// The running terminal sessions by key (the id of the item they belong to). A session here does not depend on a view:
/// it can be started before its tab was ever shown (for a view on another device) and the tab takes it over when it
/// appears. Whoever closes the item closes the session through here. Safe to use from any thread.
/// </summary>
public sealed class TerminalSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>A session was started or closed; the argument is its key. Raised on the thread that did it.</summary>
    public event Action<string>? Changed;

    /// <summary>The keys of the sessions that exist now.</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (_gate) return [.. _sessions.Keys]; }
    }

    public TerminalSession? Find(string key)
    {
        lock (_gate) return _sessions.GetValueOrDefault(key);
    }

    /// <summary>
    /// The session of <paramref name="key"/>; when there is none, starts <paramref name="spec"/> in a terminal of
    /// <paramref name="size"/> with no view. Throws when the process cannot start (nothing is kept then).
    /// </summary>
    /// <param name="created">True when this call started it.</param>
    public TerminalSession GetOrStart(string key, LaunchSpec spec, ITerminalConnectionFactory factory, TerminalSize size,
        TerminalPalette palette, ColorScheme scheme, int scrollbackLines, out bool created) =>
        GetOrStart(key, _ => new TerminalLaunch(spec), factory, size, palette, scheme, scrollbackLines, out created);

    /// <summary>
    /// The session of <paramref name="key"/>; invokes <paramref name="launchFactory"/> only when creating a new session,
    /// leaving any already-running session and its credentials untouched on reattach.
    /// </summary>
    public TerminalSession GetOrStart(string key, Func<string, TerminalLaunch> launchFactory, ITerminalConnectionFactory factory, TerminalSize size,
        TerminalPalette palette, ColorScheme scheme, int scrollbackLines, out bool created) =>
        TryGetOrStart(key, launchFactory, factory, size, palette, scheme, scrollbackLines, out created)
            ?? throw new InvalidOperationException("Terminal launch could not be planned.");

    /// <summary>
    /// Like <see cref="GetOrStart(string, Func{string, TerminalLaunch}, ITerminalConnectionFactory, TerminalSize, TerminalPalette, ColorScheme, int, out bool)"/>,
    /// returning null without keeping a session when <paramref name="launchFactory"/> returns null.
    /// </summary>
    public TerminalSession? TryGetOrStart(string key, Func<string, TerminalLaunch?> launchFactory, ITerminalConnectionFactory factory, TerminalSize size,
        TerminalPalette palette, ColorScheme scheme, int scrollbackLines, out bool created)
    {
        TerminalSession session;
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                created = false;
                return existing;
            }
            session = new TerminalSession(launchFactory, factory, size, palette, scheme, scrollbackLines, static () => false);
            try
            {
                if (!session.TryStart())
                {
                    _ = session.DisposeAsync().AsTask();
                    created = false;
                    return null;
                }
            }
            catch
            {
                _ = session.DisposeAsync().AsTask();
                throw;
            }
            _sessions[key] = session;
            created = true;
        }
        Changed?.Invoke(key);
        return session;
    }

    /// <summary>
    /// Atomically removes and returns the session of <paramref name="key"/> without disposing it or raising
    /// <see cref="Changed"/>. Callers must invoke <see cref="NotifyChanged"/> after registering ownership of the
    /// returned session so a throwing observer cannot strand it.
    /// </summary>
    public TerminalSession? TakeDetached(string key, TerminalSession? only = null)
    {
        TerminalSession? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out session) || (only is not null && !ReferenceEquals(session, only)))
            {
                return null;
            }
            _sessions.Remove(key);
        }
        return session;
    }

    /// <summary>Raises <see cref="Changed"/> for <paramref name="key"/> on the current thread.</summary>
    public void NotifyChanged(string key) => Changed?.Invoke(key);

    /// <summary>
    /// Atomically removes and returns the session of <paramref name="key"/> without disposing it; returns null when
    /// there is none or when <paramref name="only"/> does not match the current session.
    /// The caller owns disposing the returned session.
    /// </summary>
    public TerminalSession? Take(string key, TerminalSession? only = null)
    {
        var session = TakeDetached(key, only);
        if (session is not null)
        {
            NotifyChanged(key);
        }
        return session;
    }

    /// <summary>
    /// Ends the session of <paramref name="key"/> (hangup, then kill) and forgets it; nothing happens when there is none.
    /// With <paramref name="only"/>, nothing happens either when the key now belongs to another session.
    /// </summary>
    public async Task CloseAsync(string key, TerminalSession? only = null)
    {
        if (TakeDetached(key, only) is not { } session) return;
        ExceptionDispatchInfo? error = null;
        try
        {
            NotifyChanged(key);
        }
        catch (Exception ex)
        {
            error = ExceptionDispatchInfo.Capture(ex);
        }
        if (error is not null)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the primary observer failure without stranding the detached session.
            }
            error.Throw();
        }
        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Ends every session.</summary>
    public Task CloseAllAsync() => Task.WhenAll(Keys.Select(key => CloseAsync(key)));

    /// <summary>True when some session runs a program in its shell (ask before quitting).</summary>
    public async Task<bool> AnyRunningChildAsync()
    {
        TerminalSession[] sessions;
        lock (_gate) sessions = [.. _sessions.Values];
        var checks = await Task.WhenAll(sessions.Select(session => session.HasRunningChildAsync())).ConfigureAwait(false);
        return checks.Any(running => running);
    }
}
