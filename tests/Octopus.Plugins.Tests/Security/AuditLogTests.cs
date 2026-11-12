using Octopus.Core.Plugins;
using Octopus.Plugins.Security;
using Xunit;

namespace Octopus.Plugins.Tests.Security;

public sealed class AuditLogTests : IDisposable
{
    private readonly SecurityTestTree _t = new();
    private readonly string _dataDirectory;

    public AuditLogTests()
    {
        _dataDirectory = _t.Paths.DataDirectoryOf(SecurityTestTree.PluginId);
        File.Delete(Path.Combine(_dataDirectory, "audit.log"));
    }

    public void Dispose() => _t.Dispose();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private AccessDecision Decide(FileScope scope, string path, FileAccessKind access) =>
        _t.Policy.Check(_t.Grants(scope), path, access, _t.Context);

    [Fact]
    public void What_is_recorded_follows_section_7()
    {
        var project = _t.ProjectScope(FileAccessKind.ReadWrite);
        var file = Path.Combine(_t.Project, "src", "a.md");
        var data = Path.Combine(_dataDirectory, "files", "state.txt");
        var notes = Path.Combine(_t.Outside, "notes.txt");

        // Reads inside the project or a document: not recorded (too many).
        Assert.False(AuditLog.ShouldRecord(FileAccessKind.Read, Decide(project, file, FileAccessKind.Read)));
        Assert.False(AuditLog.ShouldRecord(FileAccessKind.Read, Decide(new FileScope(FileScopeKind.Document, file, FileAccessKind.Read), file, FileAccessKind.Read)));
        // Writes there: recorded.
        Assert.True(AuditLog.ShouldRecord(FileAccessKind.ReadWrite, Decide(project, file, FileAccessKind.ReadWrite)));
        // The plugin's own directories: never.
        Assert.False(AuditLog.ShouldRecord(FileAccessKind.ReadWrite, Decide(project, data, FileAccessKind.ReadWrite)));
        // Reads outside the project: recorded.
        Assert.True(AuditLog.ShouldRecord(FileAccessKind.Read, Decide(new FileScope(FileScopeKind.Picked, notes, FileAccessKind.Read), notes, FileAccessKind.Read)));
        Assert.True(AuditLog.ShouldRecord(FileAccessKind.Read, Decide(new FileScope(FileScopeKind.External, _t.Outside, FileAccessKind.Read), notes, FileAccessKind.Read)));
        // Refusals: always, even in the plugin's own scopes.
        Assert.True(AuditLog.ShouldRecord(FileAccessKind.Read, Decide(project, Path.Combine(_t.Project, ".env"), FileAccessKind.Read)));
        Assert.True(AuditLog.ShouldRecord(FileAccessKind.ReadWrite, Decide(project, Path.Combine(_t.Paths.PackageDirectoryOf(SecurityTestTree.PluginId), "main.js"), FileAccessKind.ReadWrite)));
    }

    [Fact]
    public void Entries_are_json_lines_with_time_operation_path_scope_and_result()
    {
        var time = new DateTimeOffset(2026, 11, 11, 12, 30, 0, TimeSpan.Zero);
        var log = new AuditLog(_dataDirectory, time: new FixedTime(time));
        var project = _t.ProjectScope(FileAccessKind.ReadWrite);
        var written = Path.Combine(_t.Project, "src", "a.md");
        Assert.True(log.RecordAccess("write", FileAccessKind.ReadWrite, Decide(project, written, FileAccessKind.ReadWrite), written));
        Assert.False(log.RecordAccess("read", FileAccessKind.Read, Decide(project, written, FileAccessKind.Read), written));
        var secret = Path.Combine(_t.Project, ".env");
        Assert.True(log.RecordAccess("read", FileAccessKind.Read, Decide(project, secret, FileAccessKind.Read), secret));
        Assert.True(log.RecordAccess("read", FileAccessKind.Read, Decide(project, "relative.txt", FileAccessKind.Read), "relative.txt"));
        log.Record(new AuditEntry(time, "grant", "fs.project", null, "granted"));

        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(4, lines.Length);
        if (!OperatingSystem.IsWindows()) // backslashes would be escaped in the JSON
        {
            Assert.Equal(
                $$"""{"time":"2026-11-11T12:30:00+00:00","op":"write","path":"{{written}}","scope":"project","result":"allowed"}""",
                lines[0]);
        }

        var entries = log.ReadAll();
        Assert.Equal(new AuditEntry(time, "write", written, "project", "allowed"), entries[0]);
        Assert.Equal(("denied", secret), (entries[1].Result, entries[1].Path));
        Assert.StartsWith("Sensitive: ", entries[1].Reason);
        Assert.Null(entries[1].Scope);
        Assert.Equal(("relative.txt", "denied"), (entries[2].Path, entries[2].Result));
        Assert.StartsWith("InvalidPath", entries[2].Reason);
        Assert.Equal("grant", entries[3].Operation);
    }

    [Fact]
    public void The_log_keeps_only_the_newest_entries()
    {
        var log = new AuditLog(_dataDirectory, capacity: 10);
        for (var i = 0; i < 25; i++) log.Record(new AuditEntry(DateTimeOffset.UtcNow, "write", $"/p/{i}", "project", "allowed"));
        Assert.InRange(File.ReadAllLines(log.FilePath).Length, 10, 11);
        Assert.Equal(Enumerable.Range(15, 10).Select(i => $"/p/{i}"), log.ReadAll().Select(e => e.Path));

        // A new instance (the app restarted) counts what is there and goes on trimming.
        var again = new AuditLog(_dataDirectory, capacity: 10);
        for (var i = 25; i < 40; i++) again.Record(new AuditEntry(DateTimeOffset.UtcNow, "write", $"/p/{i}", "project", "allowed"));
        Assert.InRange(File.ReadAllLines(again.FilePath).Length, 10, 11);
        Assert.Equal(Enumerable.Range(30, 10).Select(i => $"/p/{i}"), again.ReadAll().Select(e => e.Path));
    }

    [Fact]
    public void Damaged_lines_are_skipped_and_text_stays_readable()
    {
        var log = new AuditLog(_dataDirectory);
        log.Record(new AuditEntry(DateTimeOffset.UtcNow, "write", "/项目/笔记.md", "project", "allowed"));
        File.AppendAllText(log.FilePath, "{\"time\":\"half a li");
        File.AppendAllText(log.FilePath, "\n");
        log.Record(new AuditEntry(DateTimeOffset.UtcNow, "delete", "/p/x", "project", "allowed"));
        Assert.Contains("/项目/笔记.md", File.ReadAllText(log.FilePath));
        Assert.Equal(["write", "delete"], log.ReadAll().Select(e => e.Operation));
    }

    [Fact]
    public void The_log_lives_beside_the_data_scope_not_in_it()
    {
        var log = new AuditLog(_dataDirectory);
        log.Record(new AuditEntry(DateTimeOffset.UtcNow, "write", "/x", null, "allowed"));
        var grants = _t.Grants();
        Assert.False(_t.Policy.Check(grants, log.FilePath, FileAccessKind.ReadWrite, _t.Context).Allowed);
        Assert.False(_t.Policy.Check(grants, log.FilePath, FileAccessKind.Read, _t.Context).Allowed);
    }
}
