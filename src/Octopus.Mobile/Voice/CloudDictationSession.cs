using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Octopus.Mobile.Voice;

/// <summary>
/// A dictation with a cloud engine: the phone records (<see cref="IAudioCapture"/>) and a provider client streams the
/// audio away and the text back. The microphone starts first and the audio waits in a queue, so the first words are
/// not lost while the connection is still being made.
/// </summary>
internal abstract class CloudDictationSession : IDictationSession
{
    private readonly IAudioCapture _capture;
    private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly DictationFailureRelay _failures = new();
    private Task<string>? _run;
    // 0 listening, 1 stopping (finger lifted), 2 thrown away.
    private int _state;

    protected CloudDictationSession(VoiceSettings settings, IAudioCapture capture)
    {
        Settings = settings;
        _capture = capture;
        _capture.Captured += OnCaptured;
    }

    protected VoiceSettings Settings { get; }

    public event Action<float>? LevelChanged;
    public event Action<string, string>? TextChanged;
    public event Action<DictationException>? Failed
    {
        add => _failures.Add(value);
        remove => _failures.Remove(value);
    }

    /// <summary>Starts the microphone, then the provider's side in the background.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _capture.StartAsync(cancellationToken).ConfigureAwait(false);
        _run = Task.Run(RunGuardedAsync, CancellationToken.None);
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0 || _run is null) return "";
        await _capture.StopAsync().ConfigureAwait(false);
        _audio.Writer.TryComplete();
        return (await _run.WaitAsync(cancellationToken).ConfigureAwait(false)).Trim();
    }

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2) return;
        _audio.Writer.TryComplete();
        _stop.Cancel();
        _ = _capture.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        _capture.Captured -= OnCaptured;
        await _capture.DisposeAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is DictationException or TimeoutException or OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }

    /// <summary>
    /// The provider's conversation: send the audio as it comes from <paramref name="audio"/> (complete when the finger
    /// is lifted), report text with <see cref="Report"/>, and return the final text.
    /// </summary>
    protected abstract Task<string> RunAsync(ChannelReader<byte[]> audio, CancellationToken cancellationToken);

    /// <summary>The text so far: what is settled, and the tail that may still change.</summary>
    protected void Report(string settled, string tail) => TextChanged?.Invoke(settled, tail);

    /// <summary>HTTP status of a refused request or WebSocket upgrade, as the kind of error the person should see.</summary>
    protected static DictationErrorKind KindOf(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DictationErrorKind.Auth,
        null => DictationErrorKind.Network,
        _ => DictationErrorKind.Provider,
    };

    /// <summary>Connects a WebSocket and turns a refused upgrade into a <see cref="DictationException"/> of the right kind.</summary>
    protected static async Task ConnectAsync(ClientWebSocket socket, Uri uri, CancellationToken cancellationToken)
    {
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        catch (WebSocketException e)
        {
            var status = socket.HttpStatusCode == 0 ? (HttpStatusCode?)null : socket.HttpStatusCode;
            throw new DictationException(KindOf(status), status is null ? e.Message : $"HTTP {(int)status.Value}", e);
        }
    }

    private async Task<string> RunGuardedAsync()
    {
        try
        {
            return await RunAsync(_audio.Reader, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return "";
        }
        catch (DictationException e)
        {
            Fail(e);
            throw;
        }
        catch (Exception e) when (e is IOException or WebSocketException or HttpRequestException or TimeoutException)
        {
            var failure = new DictationException(DictationErrorKind.Network, e.Message, e);
            Fail(failure);
            throw failure;
        }
    }

    private void Fail(DictationException e)
    {
        // While listening nobody waits on the result: say so now. After the finger is lifted StopAsync throws it.
        if (Volatile.Read(ref _state) == 0)
        {
            _ = _capture.StopAsync();
            _failures.Emit(e);
        }
    }

    private void OnCaptured(byte[] chunk)
    {
        if (Volatile.Read(ref _state) == 2) return;
        LevelChanged?.Invoke(PcmLevel.Of(chunk));
        _audio.Writer.TryWrite(chunk);
    }
}
