using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Octopus.SignalForwarder;

/// <summary>
/// Keeps a chatty source (a status line refreshes several times a second) from flooding Octopus. Each forwarder is
/// a new process, so the last send per item and channel is kept in a small file.
/// </summary>
internal sealed class SignalThrottle(string directory)
{
    /// <summary>The same payload again within this window is never resent, whatever the minimum interval.</summary>
    public const int RepeatWindowMs = 2000;

    public static SignalThrottle ForCurrentUser() =>
        new(Path.Combine(Path.GetTempPath(), $"octopus-signal-{Environment.UserName}"));

    public bool ShouldSend(string item, string channel, string payload, int minIntervalMs, long nowMs)
    {
        var file = Path.Combine(directory, Hash($"{item}\n{channel}")[..24]);
        var payloadHash = Hash(payload);
        try
        {
            if (File.Exists(file))
            {
                var parts = File.ReadAllText(file).Split(' ');
                if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last))
                {
                    var elapsed = nowMs - last;
                    if (elapsed >= 0 && (elapsed < minIntervalMs || (elapsed < RepeatWindowMs && parts[1] == payloadHash)))
                    {
                        return false;
                    }
                }
            }
            Directory.CreateDirectory(directory);
            File.WriteAllText(file, $"{nowMs.ToString(CultureInfo.InvariantCulture)} {payloadHash}");
        }
        catch (IOException)
        {
            // An unwritable temp directory only costs a duplicate signal.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return true;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
