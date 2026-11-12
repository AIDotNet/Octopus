using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Octopus.Core.Plugins;

namespace Octopus.Plugins.Security;

/// <summary>One line of a plugin's audit log.</summary>
/// <param name="Operation">What was attempted: <c>read</c>, <c>write</c>, <c>delete</c>, <c>rename</c>, <c>copy</c>, <c>createDirectory</c>, <c>grant</c>, <c>revoke</c>…</param>
/// <param name="Path">The real path (the one that was checked), or what the plugin passed when it was not a valid path.</param>
/// <param name="Scope">The scope kind that allowed it (<c>project</c>, <c>picked</c>…), or null.</param>
/// <param name="Result"><c>allowed</c>, <c>denied</c>, or for grants what happened (<c>granted</c>, <c>revoked</c>, <c>declined</c>).</param>
/// <param name="Reason">Why it was denied (the <see cref="AccessDenial"/> code and text), or other detail.</param>
public sealed record AuditEntry(DateTimeOffset Time, string Operation, string Path, string? Scope, string Result, string? Reason = null);

/// <summary>
/// A plugin's file-operation record, <c>plugin-data/&lt;id&gt;/audit.log</c> (design/plugins/04 §7): JSON lines, the
/// newest last, trimmed to the last <see cref="Capacity"/> entries. It lives next to the plugin's data scope
/// (<c>files/</c>), not in it, so the plugin cannot edit its own record. Thread-safe.
/// </summary>
public sealed class AuditLog
{
    public const int DefaultCapacity = 5000;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private int _count = -1; // lines in the file; counted on first use

    /// <param name="pluginDataDirectory"><c>plugin-data/&lt;id&gt;</c> (<see cref="PluginPaths.DataDirectoryOf"/>).</param>
    /// <param name="capacity">Entries kept.</param>
    public AuditLog(string pluginDataDirectory, int capacity = DefaultCapacity, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        FilePath = Path.Combine(pluginDataDirectory, "audit.log");
        Capacity = capacity;
        _time = time ?? TimeProvider.System;
    }

    public string FilePath { get; }

    public int Capacity { get; }

    /// <summary>
    /// What §7 records: every refusal; writes, deletes, renames, copies and new directories; reads outside a project
    /// or document. Not recorded: anything in the plugin's own <c>plugin</c>, <c>data</c> and <c>tmp</c> scopes, and
    /// reads in <c>project</c>, <c>document</c> and <c>agentData</c> scopes (too many to be useful: a usage plugin reads
    /// hundreds of session files per refresh and would push everything else out of the log).
    /// </summary>
    public static bool ShouldRecord(FileAccessKind access, AccessDecision decision)
    {
        if (!decision.Allowed) return true;
        var kind = decision.Scope!.Kind;
        if (kind is FileScopeKind.Plugin or FileScopeKind.Data or FileScopeKind.Tmp) return false;
        if (access == FileAccessKind.ReadWrite) return true;
        return kind is not (FileScopeKind.Project or FileScopeKind.Document or FileScopeKind.AgentData);
    }

    /// <summary>Records a file access when <see cref="ShouldRecord"/> says so. True when a line was written.</summary>
    /// <param name="requestedPath">What the plugin passed; logged only when the decision has no real path.</param>
    public bool RecordAccess(string operation, FileAccessKind access, AccessDecision decision, string requestedPath)
    {
        if (!ShouldRecord(access, decision)) return false;
        Record(new AuditEntry(
            _time.GetUtcNow(),
            operation,
            decision.RealPath ?? requestedPath,
            decision.Scope is { } scope ? ScopeName(scope.Kind) : null,
            decision.Allowed ? "allowed" : "denied",
            decision.Allowed ? null : $"{decision.Denial}: {decision.Reason}"));
        return true;
    }

    /// <summary>Appends an entry unconditionally (grants, revocations, runtime requests and their outcome).</summary>
    public void Record(AuditEntry entry)
    {
        var line = Serialize(entry);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (_count < 0) _count = CountLines();
            File.AppendAllText(FilePath, line, Encoding.UTF8);
            _count++;
            // Trim in batches, not on every line: rewriting 5,000 lines per access would cost more than the access.
            if (_count > Capacity + Math.Max(1, Capacity / 10)) Trim();
        }
    }

    /// <summary>The entries, oldest first. Lines that cannot be read (a crash mid-write) are skipped.</summary>
    public IReadOnlyList<AuditEntry> ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return [];
            var entries = new List<AuditEntry>();
            foreach (var line in File.ReadLines(FilePath))
            {
                if (TryParse(line) is { } entry) entries.Add(entry);
            }
            return entries.Count > Capacity ? entries[^Capacity..] : entries;
        }
    }

    private void Trim()
    {
        var keep = File.ReadLines(FilePath).TakeLast(Capacity).ToList();
        var temp = FilePath + ".tmp";
        File.WriteAllLines(temp, keep, new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
        _count = keep.Count;
    }

    private int CountLines()
    {
        if (!File.Exists(FilePath)) return 0;
        var count = 0;
        foreach (var _ in File.ReadLines(FilePath)) count++;
        return count;
    }

    private static string Serialize(AuditEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("time", entry.Time.ToUniversalTime());
            writer.WriteString("op", entry.Operation);
            writer.WriteString("path", entry.Path);
            if (entry.Scope is not null) writer.WriteString("scope", entry.Scope);
            writer.WriteString("result", entry.Result);
            if (entry.Reason is not null) writer.WriteString("reason", entry.Reason);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static AuditEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new AuditEntry(
                root.GetProperty("time").GetDateTimeOffset(),
                root.GetProperty("op").GetString() ?? "",
                root.GetProperty("path").GetString() ?? "",
                root.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
                root.GetProperty("result").GetString() ?? "",
                root.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string ScopeName(FileScopeKind kind) => kind switch
    {
        FileScopeKind.AgentData => "agentData",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
