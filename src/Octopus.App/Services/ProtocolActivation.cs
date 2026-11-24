using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Octopus.App.Services;

/// <summary>
/// Handles <c>octopus://</c> protocol activation across desktop platforms (e.g. <c>octopus://auth-done</c> after
/// browser sign-in). On Windows and Linux, the OS launches a second process with the URI as an argument; this bridge
/// forwards it to the already-running instance over a per-user named pipe and lets the helper process exit immediately.
/// </summary>
public static class ProtocolActivation
{
    private const int MaxUriLength = 4096;

    /// <summary>Per-user deterministic pipe name so different OS users do not collide.</summary>
    public static string DefaultPipeName { get; } = BuildPipeName(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Returns the first valid <c>octopus://</c> URI in <paramref name="args"/>, or null.</summary>
    public static Uri? TryParseProtocolUri(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            var trimmed = arg.Trim();
            if (!trimmed.StartsWith("octopus://", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && string.Equals(uri.Scheme, "octopus", StringComparison.OrdinalIgnoreCase))
            {
                return uri;
            }
        }
        return null;
    }

    /// <summary>Forwards <paramref name="uri"/> to an already-running Octopus instance. Returns true if delivered.</summary>
    public static bool TryForwardToRunningInstance(Uri uri, string? pipeName = null, int timeoutMs = 400)
    {
        if (!string.Equals(uri.Scheme, "octopus", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var payload = Encoding.UTF8.GetBytes(uri.AbsoluteUri + "\n");
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Starts listening for incoming <c>octopus://</c> URIs forwarded by newly spawned helper processes.</summary>
    public static IDisposable StartServer(Action<Uri> onUri, string? pipeName = null) =>
        new Server(pipeName ?? DefaultPipeName, onUri);

    private static string BuildPipeName(string user, string profile)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{user}\0{profile}"));
        // Keep short so the underlying Unix domain socket path ($TMPDIR/CoreFxPipe_<name>) stays well under macOS's 104-byte limit.
        return "oct-uri-" + Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    private sealed class Server : IDisposable
    {
        private readonly string _pipeName;
        private readonly Action<Uri> _onUri;
        private readonly CancellationTokenSource _cts = new();

        public Server(string pipeName, Action<Uri> onUri)
        {
            _pipeName = pipeName;
            _onUri = onUri;
            _ = Task.Run(ListenLoopAsync);
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    var raw = await ReadLineLimitedAsync(pipe, _cts.Token).ConfigureAwait(false);
                    if (raw is not null
                        && Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
                        && string.Equals(uri.Scheme, "octopus", StringComparison.OrdinalIgnoreCase))
                    {
                        _onUri(uri);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    try { await Task.Delay(250, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                finally
                {
                    if (pipe is not null)
                    {
                        try { await pipe.DisposeAsync().ConfigureAwait(false); }
                        catch { }
                    }
                }
            }
        }

        private static async Task<string?> ReadLineLimitedAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[MaxUriLength];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var newline = Array.IndexOf(buffer, (byte)'\n', total, read);
                if (newline >= 0)
                {
                    total = newline;
                    break;
                }
                total += read;
            }
            return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
