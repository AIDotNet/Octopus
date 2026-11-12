using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Octopus.Platform;

namespace Octopus.Agents.Signals;

/// <summary>
/// The server side of <c>octopus-signal</c>. The endpoint is usable by the current user only: a Unix domain socket
/// inside a 0700 directory under the cache directory, or a named pipe whose ACL admits only the current user. On top
/// of that every envelope carries a random token created per run and handed to agents through their environment,
/// so another local program that finds the socket still cannot inject signals.
/// </summary>
public sealed class SignalHub : ISignalHub
{
    /// <summary>The forwarder drops payloads above this, so a larger envelope is not one of ours.</summary>
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    /// <summary>Payload plus room for the envelope's other members.</summary>
    public const int MaxEnvelopeBytes = MaxPayloadBytes + 64 * 1024;

    internal const string SocketFileName = "hub.sock";
    private const string OwnerFileName = "owner";
    private const string RootDirectoryName = "signal-hub";
    private const string PipePrefix = @"\\.\pipe\";

    /// <summary>sun_path holds 104 bytes on macOS and 108 on Linux, including the terminator.</summary>
    private const int MaxSocketPathBytes = 100;

    /// <summary>A forwarder writes in well under a second; anything slower is stuck or hostile.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A burst of hooks from several tabs fits; a flood is dropped rather than queued.</summary>
    private const int MaxConcurrentConnections = 32;

    private readonly IAppPaths _paths;
    private readonly IAgentCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _connections = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly ConcurrentDictionary<(string Agent, string Channel, string Item), AgentSignal> _last = new();
    private readonly ConcurrentDictionary<(string Agent, string Key), AgentAccount> _accounts = new();
    private Subscription[] _subscriptions = [];
    private SignalEndpoint? _endpoint;
    private byte[] _tokenBytes = [];
    private Socket? _listener;
    private NamedPipeServerStream? _listeningPipe;
    private string? _directory;
    private long _received;
    private long _dropped;
    private bool _disposed;

    public SignalHub(IAppPaths paths, IAgentCatalog catalog, TimeProvider? time = null)
    {
        _paths = paths;
        _catalog = catalog;
        _time = time ?? TimeProvider.System;
    }

    public SignalEndpoint? Endpoint
    {
        get { lock (_gate) { return _endpoint; } }
    }

    public SignalHubStatistics Statistics => new(Interlocked.Read(ref _received), Interlocked.Read(ref _dropped));

    /// <summary>The directory that holds the socket (Unix only); for tests and diagnostics.</summary>
    internal string? SocketDirectory
    {
        get { lock (_gate) { return _directory; } }
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
            if (_endpoint is not null)
            {
                return true;
            }
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            try
            {
                var address = OperatingSystem.IsWindows() ? StartPipe() : StartSocket();
                _tokenBytes = Encoding.UTF8.GetBytes(token);
                _endpoint = new SignalEndpoint(address, token);
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                CloseEndpoint();
                return false;
            }
        }
    }

    public IDisposable Subscribe(string? agentId, string? channel, Action<AgentSignal> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var subscription = new Subscription(this, agentId, channel, listener);
        lock (_gate)
        {
            _subscriptions = [.. _subscriptions, subscription];
        }
        return subscription;
    }

    public AgentSignal? LastSignal(string agentId, string channel, string? itemId = null)
    {
        if (itemId is not null)
        {
            return _last.TryGetValue((Canonical(agentId), channel, itemId), out var exact) ? exact : null;
        }
        var agent = Canonical(agentId);
        AgentSignal? latest = null;
        foreach (var ((a, c, _), signal) in _last)
        {
            if (a == agent && c == channel && (latest is null || signal.ReceivedAt > latest.ReceivedAt))
            {
                latest = signal;
            }
        }
        return latest;
    }

    public void ForgetItem(string itemId)
    {
        foreach (var key in _last.Keys)
        {
            if (key.Item == itemId)
            {
                _last.TryRemove(key, out _);
            }
        }
    }

    public IReadOnlyList<AgentAccount> Accounts(string agentId)
    {
        var agent = Canonical(agentId);
        return _accounts.Values.Where(a => a.AgentId == agent).OrderBy(a => a.Directory, StringComparer.Ordinal).ToList();
    }

    public string RegisterAccount(string agentId, string directory)
    {
        var definition = _catalog.Find(agentId);
        if (definition is null) return "";
        var resolved = SignalAccounts.ResolveDirectory(definition, directory);
        var key = SignalAccounts.KeyOfDirectory(resolved);
        if (key.Length > 0)
        {
            _accounts.TryAdd((definition.Id, key), new AgentAccount(definition.Id, key, resolved, SignalAccounts.DefaultLabel(resolved)));
        }
        return key;
    }

    public void Publish(AgentSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        Deliver(signal with { AgentId = Canonical(signal.AgentId) });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _stop.Cancel();
            CloseEndpoint();
            _subscriptions = [];
        }
    }

    /// <summary>Checks an envelope and turns it into a signal; null (and counted as dropped) when anything is off.</summary>
    internal AgentSignal? Accept(ReadOnlySpan<byte> bytes)
    {
        var envelope = SignalEnvelopeReader.TryRead(bytes);
        byte[] expected;
        lock (_gate)
        {
            expected = _tokenBytes;
        }
        if (envelope is null
            || envelope.Version != SignalEnvelopeReader.Version
            || expected.Length == 0
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(envelope.Token), expected))
        {
            return null;
        }
        // Only declared channels exist: plugin permissions are granted per declared channel (design/plugins/11 §1.5).
        var definition = _catalog.Find(envelope.Agent);
        if (definition?.Signals is null || !definition.Signals.Channels.ContainsKey(envelope.Channel))
        {
            return null;
        }
        var directory = SignalAccounts.ResolveDirectory(definition, envelope.Account);
        var key = SignalAccounts.KeyOfDirectory(directory);
        if (key.Length > 0)
        {
            _accounts.TryAdd((definition.Id, key), new AgentAccount(definition.Id, key, directory, SignalAccounts.DefaultLabel(directory)));
        }
        return new AgentSignal(
            definition.Id,
            envelope.Channel,
            envelope.Item,
            key,
            envelope.Timestamp,
            _time.GetUtcNow().ToUnixTimeMilliseconds(),
            envelope.Payload);
    }

    private void Deliver(AgentSignal signal)
    {
        _last[(signal.AgentId, signal.Channel, signal.ItemId)] = signal;
        Subscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = _subscriptions;
        }
        foreach (var subscription in subscriptions)
        {
            if (!subscription.Matches(signal))
            {
                continue;
            }
            try
            {
                subscription.Listener(signal);
            }
            catch (Exception)
            {
                // A subscriber's bug must not stop delivery to the others or reach the accept loop.
            }
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions = _subscriptions.Where(s => !ReferenceEquals(s, subscription)).ToArray();
        }
    }

    private string Canonical(string agentId) => _catalog.Find(agentId)?.Id ?? agentId;

    // ---- Unix domain socket ----

    [UnsupportedOSPlatform("windows")]
    private string StartSocket()
    {
        var root = Path.Combine(_paths.CacheDirectory, RootDirectoryName);
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RemoveStaleDirectories(root);

        var directory = CreatePrivateDirectory(root);
        var path = Path.Combine(directory, SocketFileName);
        if (Encoding.UTF8.GetByteCount(path) > MaxSocketPathBytes)
        {
            // A long home or cache path does not fit in sun_path; the per-user temporary directory usually does.
            TryDelete(directory);
            directory = Directory.CreateTempSubdirectory("octosig").FullName; // created 0700
            path = Path.Combine(directory, SocketFileName);
            if (Encoding.UTF8.GetByteCount(path) > MaxSocketPathBytes)
            {
                TryDelete(directory);
                throw new IOException("No directory short enough for a Unix domain socket.");
            }
        }
        _directory = directory;
        File.WriteAllText(Path.Combine(directory, OwnerFileName), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(64);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;
        var token = _stop.Token;
        _ = Task.Run(() => AcceptSocketsAsync(listener, token));
        return path;
    }

    [UnsupportedOSPlatform("windows")]
    private static string CreatePrivateDirectory(string root)
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var directory = Path.Combine(root, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
            if (Directory.Exists(directory))
            {
                continue;
            }
            Directory.CreateDirectory(directory, Private);
            // The umask may only remove bits, but an existing directory keeps its own mode: set it explicitly.
            File.SetUnixFileMode(directory, Private);
            return directory;
        }
        throw new IOException("Could not create a private directory for the signal socket.");
    }

    /// <summary>Directories left by an Octopus that did not exit cleanly: their owner process is gone.</summary>
    private static void RemoveStaleDirectories(string root)
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(root))
            {
                var owner = Path.Combine(directory, OwnerFileName);
                if (File.Exists(owner)
                    && int.TryParse(File.ReadAllText(owner).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    && IsRunning(pid))
                {
                    continue;
                }
                if (!File.Exists(owner) && Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-1))
                {
                    continue; // being created right now by another instance
                }
                TryDelete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task AcceptSocketsAsync(Socket listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                // Out of descriptors, an aborted connection…: never let the loop die.
                try
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            Dispatch(new NetworkStream(client, ownsSocket: true), token);
        }
    }

    // ---- Windows named pipe ----

    [SupportedOSPlatform("windows")]
    private string StartPipe()
    {
        var name = "octopus-signal-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        // FirstPipeInstance fails if anyone created the name before us, so nobody can sit in front of the hub.
        var first = CreatePipe(name, first: true);
        _listeningPipe = first;
        var token = _stop.Token;
        _ = Task.Run(() => AcceptPipesAsync(name, first, token));
        return PipePrefix + name;
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreatePipe(string name, bool first) => new(
        name,
        PipeDirection.In,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    [SupportedOSPlatform("windows")]
    private async Task AcceptPipesAsync(string name, NamedPipeServerStream server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                server.Dispose();
                return;
            }
            catch (Exception)
            {
                server.Dispose();
                if (await NextPipeAsync(name, token).ConfigureAwait(false) is not { } replacement)
                {
                    return;
                }
                server = replacement;
                continue;
            }
            var connected = server;
            // Open the next instance before reading, so the next forwarder does not find the pipe busy.
            var next = await NextPipeAsync(name, token).ConfigureAwait(false);
            Dispatch(connected, token);
            if (next is null)
            {
                return;
            }
            server = next;
        }
        server.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private async Task<NamedPipeServerStream?> NextPipeAsync(string name, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var pipe = CreatePipe(name, first: false);
                lock (_gate)
                {
                    if (_disposed)
                    {
                        pipe.Dispose();
                        return null;
                    }
                    _listeningPipe = pipe;
                }
                return pipe;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    // ---- Connections ----

    private void Dispatch(Stream stream, CancellationToken token)
    {
        if (!_connections.Wait(0))
        {
            Interlocked.Increment(ref _dropped);
            stream.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await using (stream.ConfigureAwait(false))
                {
                    var bytes = await ReadEnvelopeAsync(stream, token).ConfigureAwait(false);
                    var signal = bytes is null ? null : Accept(bytes);
                    if (signal is null)
                    {
                        Interlocked.Increment(ref _dropped);
                        return;
                    }
                    Interlocked.Increment(ref _received);
                    Deliver(signal);
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _dropped);
            }
            finally
            {
                _connections.Release();
            }
        }, CancellationToken.None);
    }

    /// <summary>Everything the client sends until it closes its side; null when too large, too slow or cancelled.</summary>
    private static async Task<byte[]?> ReadEnvelopeAsync(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > MaxEnvelopeBytes)
                {
                    return null;
                }
                buffer.Write(chunk, 0, n);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            return null;
        }
        return buffer.ToArray();
    }

    private void CloseEndpoint()
    {
        _endpoint = null;
        _tokenBytes = [];
        try
        {
            _listener?.Dispose();
        }
        catch (Exception)
        {
        }
        _listener = null;
        try
        {
            _listeningPipe?.Dispose();
        }
        catch (Exception)
        {
        }
        _listeningPipe = null;
        if (_directory is not null)
        {
            TryDelete(_directory);
            _directory = null;
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Subscription(SignalHub hub, string? agentId, string? channel, Action<AgentSignal> listener) : IDisposable
    {
        private SignalHub? _hub = hub;

        public Action<AgentSignal> Listener { get; } = listener;

        public bool Matches(AgentSignal signal) =>
            (agentId is null || string.Equals(agentId, signal.AgentId, StringComparison.OrdinalIgnoreCase))
            && (channel is null || string.Equals(channel, signal.Channel, StringComparison.Ordinal));

        public void Dispose() => Interlocked.Exchange(ref _hub, null)?.Unsubscribe(this);
    }
}
