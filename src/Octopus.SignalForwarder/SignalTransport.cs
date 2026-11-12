using System.IO.Pipes;
using System.Net.Sockets;

namespace Octopus.SignalForwarder;

/// <summary>
/// Delivers an envelope to Octopus: a Unix domain socket on macOS and Linux, a named pipe (<c>\\.\pipe\name</c>) on
/// Windows. Gives up quietly when nobody listens.
/// </summary>
internal static class SignalTransport
{
    private const string PipePrefix = @"\\.\pipe\";

    public static async Task<bool> TrySendAsync(string endpoint, byte[] envelope, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            if (endpoint.StartsWith(PipePrefix, StringComparison.Ordinal))
            {
                await using var pipe = new NamedPipeClientStream(".", endpoint[PipePrefix.Length..], PipeDirection.Out);
                await pipe.ConnectAsync(cts.Token);
                await pipe.WriteAsync(envelope, cts.Token);
                await pipe.FlushAsync(cts.Token);
                return true;
            }
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cts.Token);
            var sent = 0;
            while (sent < envelope.Length)
            {
                sent += await socket.SendAsync(envelope.AsMemory(sent), SocketFlags.None, cts.Token);
            }
            socket.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
