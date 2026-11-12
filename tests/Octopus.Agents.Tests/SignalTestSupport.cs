using System.Net.Sockets;
using System.Text;
using Octopus.Agents.Signals;

namespace Octopus.Agents.Tests;

/// <summary>A clock the tests move by hand; timers still run on real time.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// A temporary root short enough for a Unix socket path under it (macOS limits sun_path to 104 bytes and its
/// per-user temp directory is already ~50).
/// </summary>
internal sealed class ShortTempDir : IDisposable
{
    public string Path { get; }

    public ShortTempDir()
    {
        var name = "oct" + Guid.NewGuid().ToString("N")[..8];
        Path = OperatingSystem.IsWindows() ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), name) : System.IO.Path.Combine("/tmp", name);
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class SignalTestHelpers
{
    /// <summary>Writes an executable /bin/sh script; the calling test is skipped on Windows.</summary>
    public static string Script(string directory, string name, string body)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fake agents are /bin/sh scripts");
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    /// <summary>Sends raw bytes the way the forwarder does: connect, write, close the sending side.</summary>
    public static async Task SendAsync(string socketPath, byte[] bytes)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        var sent = 0;
        while (sent < bytes.Length)
        {
            sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None);
        }
        socket.Shutdown(SocketShutdown.Send);
    }

    public static Task SendAsync(string socketPath, string text) => SendAsync(socketPath, Encoding.UTF8.GetBytes(text));

    /// <summary>The forwarder's envelope (design/plugins/13 §2), built by hand so the test does not depend on our reader.</summary>
    public static string Envelope(string token, string agent, string channel, string payload, string item = "item-1", string account = "", int version = 1) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["v"] = version,
            ["token"] = token,
            ["item"] = item,
            ["agent"] = agent,
            ["channel"] = channel,
            ["account"] = account,
            ["ts"] = 1794069588307L,
            ["payload"] = payload,
        });

    /// <summary>Waits until <paramref name="condition"/> holds, polling; fails after a few seconds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("timed out waiting for " + what);
            }
            await Task.Delay(20);
        }
    }

    /// <summary>The first signal a subscription receives.</summary>
    public static (IDisposable Subscription, Task<AgentSignal> First) Next(ISignalHub hub, string? agent, string? channel)
    {
        var tcs = new TaskCompletionSource<AgentSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = hub.Subscribe(agent, channel, s => tcs.TrySetResult(s));
        return (subscription, tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>A program on PATH (tests that need node skip without it).</summary>
    public static string? FindOnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>The repository root, found by walking up to Octopus.slnx; null when the tests run elsewhere.</summary>
    public static string? RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Octopus.slnx")))
            {
                return directory.FullName;
            }
        }
        return null;
    }
}
