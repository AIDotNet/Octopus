using System.Text.Json;
using Octopus.Core.Settings;
using Octopus.Platform;

namespace Octopus.Storage;

/// <summary>
/// Stores settings in <c>&lt;DataDirectory&gt;/settings.json</c>. <see cref="Update"/> changes <see cref="Current"/> at once;
/// the file is written a moment later on a pool thread, so a spinner or a typed number does not wait on the disk at every
/// step. <see cref="Flush"/> (and disposing) writes a pending change now. Write failures are recorded in
/// <see cref="LastError"/>, never thrown.
/// </summary>
public sealed class JsonSettingsStore(IAppPaths paths) : ISettingsStore, IDisposable
{
    /// <summary>How long the file waits after a change; the changes made within it are written once.</summary>
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _path = Path.Combine(paths.DataDirectory, "settings.json");
    /// <summary>Guards <see cref="Current"/>, the pending flag and the timer.</summary>
    private readonly object _gate = new();
    /// <summary>One write at a time; each takes the latest settings once it holds this, so writes never land out of order.</summary>
    private readonly object _writeGate = new();
    private Timer? _timer;
    private bool _dirty;
    private bool _disposed;

    public AppSettingsModel Current { get; private set; } = new();

    /// <summary>The last failure to write the file; null when the last write succeeded.</summary>
    public Exception? LastError { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        // A change not on disk yet would otherwise be lost to the older file.
        Flush();
        lock (_gate)
        {
            Current = Read();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettingsModel> mutate)
    {
        var writeNow = false;
        lock (_gate)
        {
            mutate(Current);
            Current = SettingsNormalizer.Normalize(Current);
            _dirty = true;
            if (_disposed)
            {
                // No timer any more (the app is ending): the change goes to disk right away.
                writeNow = true;
            }
            else
            {
                _timer ??= new Timer(_ => WritePending());
                _timer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
            }
        }
        if (writeNow)
        {
            WritePending();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Flush()
    {
        lock (_gate)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        WritePending();
    }

    /// <summary>Writes a pending change. The container disposes the store at the end of the app (AppHost.Reset).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        WritePending();
    }

    private void WritePending()
    {
        lock (_writeGate)
        {
            string text;
            lock (_gate)
            {
                if (!_dirty)
                {
                    return;
                }
                _dirty = false;
                // Under the gate: Update mutates the same object.
                text = JsonSerializer.Serialize(Current, OctopusJsonContext.FileOptions.GetTypeInfo<AppSettingsModel>());
            }
            try
            {
                AtomicFile.WriteAllText(_path, text);
                LastError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = e;
                // Tried again by the next change or flush.
                lock (_gate)
                {
                    _dirty = true;
                }
            }
        }
    }

    private AppSettingsModel Read()
    {
        if (!File.Exists(_path) || !AtomicFile.TryReadAllText(_path, out var text)) return new AppSettingsModel();
        try
        {
            var settings = JsonSerializer.Deserialize(text, OctopusJsonContext.FileOptions.GetTypeInfo<AppSettingsModel>());
            return SettingsNormalizer.Normalize(settings);
        }
        catch (JsonException)
        {
            AtomicFile.Quarantine(_path);
            return new AppSettingsModel();
        }
    }
}
