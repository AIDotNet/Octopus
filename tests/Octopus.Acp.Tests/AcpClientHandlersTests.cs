using System.Text;
using Octopus.Acp.Client;
using Octopus.Acp.Transport;

namespace Octopus.Acp.Tests;

public class AcpClientHandlersTests
{
    [Fact]
    public async Task FileSystemHandler_reads_slices_uses_unsaved_buffer_writes_atomically_and_enforces_roots()
    {
        var rootDir = Directory.CreateTempSubdirectory("octopus-acp-fs-root-").FullName;
        var outsideDir = Directory.CreateTempSubdirectory("octopus-acp-fs-out-").FullName;
        try
        {
            var filePath = Path.Combine(rootDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\nline4\nline5");

            var unsaved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var writtenEvents = new List<(string Path, string Content)>();
            var handler = new AcpFileSystemHandler(
                allowedRoots: [rootDir],
                tryReadUnsavedBuffer: p => unsaved.TryGetValue(p, out var txt) ? txt : null,
                onFileWritten: (p, c) => writtenEvents.Add((p, c)));

            // Full read & 1-based line/limit slicing.
            Assert.Equal("line1\nline2\nline3\nline4\nline5", await handler.ReadTextFileAsync("s1", filePath));
            Assert.Equal("line2\nline3", await handler.ReadTextFileAsync("s1", filePath, line: 2, limit: 2));
            Assert.Equal("line4\nline5", await handler.ReadTextFileAsync("s1", filePath, line: 4));
            Assert.Equal("", await handler.ReadTextFileAsync("s1", filePath, line: 10));
            Assert.Equal("", await handler.ReadTextFileAsync("s1", filePath, line: 1, limit: 0));

            // Unsaved editor buffer takes precedence over disk.
            unsaved[Path.GetFullPath(filePath)] = "dirty1\ndirty2\ndirty3";
            Assert.Equal("dirty2", await handler.ReadTextFileAsync("s1", filePath, line: 2, limit: 1));

            // Atomic write creates missing parent directories & notifies observer.
            var nestedPath = Path.Combine(rootDir, "sub", "deep", "created.txt");
            await handler.WriteTextFileAsync("s1", nestedPath, "hello-acp");
            Assert.Equal("hello-acp", await File.ReadAllTextAsync(nestedPath));
            var wrote = Assert.Single(writtenEvents);
            Assert.Equal(Path.GetFullPath(nestedPath), wrote.Path);
            Assert.Equal("hello-acp", wrote.Content);

            // Validation & JSON-RPC error codes.
            var relEx = await Assert.ThrowsAsync<AcpRpcException>(() => handler.ReadTextFileAsync("s1", "relative.txt"));
            Assert.Equal(-32602, relEx.Code);

            var badLineEx = await Assert.ThrowsAsync<AcpRpcException>(() => handler.ReadTextFileAsync("s1", filePath, line: 0));
            Assert.Equal(-32602, badLineEx.Code);

            var badLimitEx = await Assert.ThrowsAsync<AcpRpcException>(() => handler.ReadTextFileAsync("s1", filePath, limit: -1));
            Assert.Equal(-32602, badLimitEx.Code);

            var missingEx = await Assert.ThrowsAsync<AcpRpcException>(() =>
                handler.ReadTextFileAsync("s1", Path.Combine(rootDir, "does-not-exist.txt")));
            Assert.Equal(-32002, missingEx.Code);

            var outsidePath = Path.Combine(outsideDir, "secret.txt");
            await File.WriteAllTextAsync(outsidePath, "nope");
            var deniedRead = await Assert.ThrowsAsync<AcpRpcException>(() => handler.ReadTextFileAsync("s1", outsidePath));
            Assert.Equal(-32001, deniedRead.Code);

            var deniedWrite = await Assert.ThrowsAsync<AcpRpcException>(() => handler.WriteTextFileAsync("s1", outsidePath, "x"));
            Assert.Equal(-32001, deniedWrite.Code);
        }
        finally
        {
            try { Directory.Delete(rootDir, true); } catch { }
            try { Directory.Delete(outsideDir, true); } catch { }
        }
    }

    [Fact]
    public async Task TerminalHandler_runs_waits_snapshots_truncates_utf8_kills_and_releases()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-acp-term-").FullName;
        try
        {
            await using var handler = new AcpTerminalHandler(tempDir);

            // 1. Run command with custom env, wait for exit, inspect output + live snapshot, then release.
            string termId;
            if (OperatingSystem.IsWindows())
            {
                termId = await handler.CreateAsync(
                    "s1",
                    "cmd.exe",
                    ["/c", "echo hello-%ACP_TEST_VAR%"],
                    new Dictionary<string, string> { ["ACP_TEST_VAR"] = "world" },
                    tempDir);
            }
            else
            {
                termId = await handler.CreateAsync(
                    "s1",
                    "/bin/sh",
                    ["-c", "printf 'hello-%s' \"$ACP_TEST_VAR\""],
                    new Dictionary<string, string> { ["ACP_TEST_VAR"] = "world" },
                    tempDir);
            }

            var exit = await handler.WaitForExitAsync("s1", termId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exit.ExitCode);

            var output = await handler.GetOutputAsync("s1", termId);
            Assert.Contains("hello-world", output.Output);
            Assert.False(output.Truncated);
            Assert.Equal(0, output.ExitStatus?.ExitCode);

            var snap = handler.TryGetSnapshot(termId);
            Assert.NotNull(snap);
            Assert.Equal(termId, snap!.TerminalId);
            Assert.Contains("hello-world", snap.Output);
            Assert.Equal(0, snap.ExitStatus?.ExitCode);

            await handler.ReleaseAsync("s1", termId);
            Assert.Null(handler.TryGetSnapshot(termId));
            var unknownEx = await Assert.ThrowsAsync<AcpRpcException>(() => handler.GetOutputAsync("s1", termId));
            Assert.Equal(-32002, unknownEx.Code);

            // 2. UTF-8-boundary-safe byte-limited truncation (no replacement char U+FFFD).
            if (!OperatingSystem.IsWindows())
            {
                // 300 * 12 bytes ("你好世界" = 12 UTF-8 bytes) = 3600 bytes > 1024 limit.
                var utf8Id = await handler.CreateAsync(
                    "s1",
                    "/bin/sh",
                    ["-c", "i=0; while [ $i -lt 300 ]; do printf '你好世界'; i=$((i+1)); done"],
                    cwd: tempDir,
                    outputByteLimit: 1024);

                var utf8Exit = await handler.WaitForExitAsync("s1", utf8Id).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, utf8Exit.ExitCode);

                var utf8Out = await handler.GetOutputAsync("s1", utf8Id);
                Assert.True(utf8Out.Truncated);
                Assert.True(Encoding.UTF8.GetByteCount(utf8Out.Output) <= 1024);
                Assert.DoesNotContain('\uFFFD', utf8Out.Output);
                Assert.EndsWith("你好世界", utf8Out.Output);
                await handler.ReleaseAsync("s1", utf8Id);

                // 3. KillAsync terminates long-running process while keeping output/exitStatus readable until ReleaseAsync.
                var longId = await handler.CreateAsync(
                    "s1",
                    "/bin/sh",
                    ["-c", "echo started; sleep 30"],
                    cwd: tempDir);

                // Wait briefly for initial output before killing.
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline)
                {
                    var probe = await handler.GetOutputAsync("s1", longId);
                    if (probe.Output.Contains("started")) break;
                    await Task.Delay(25);
                }

                await handler.KillAsync("s1", longId);
                var killExit = await handler.WaitForExitAsync("s1", longId).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("SIGKILL", killExit.Signal);

                var afterKill = await handler.GetOutputAsync("s1", longId);
                Assert.Contains("started", afterKill.Output);
                Assert.Equal("SIGKILL", afterKill.ExitStatus?.Signal);
                await handler.ReleaseAsync("s1", longId);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
