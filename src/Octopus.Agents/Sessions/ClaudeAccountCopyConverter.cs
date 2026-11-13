using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Octopus.Agents.Sessions;

/// <summary>
/// Moves a Claude Code session to another Claude account. Claude resumes a session by id from the account folder it
/// runs with (<c>CLAUDE_CONFIG_DIR</c>), so the move is a copy of the transcript into the target's project folder.
/// </summary>
public sealed class ClaudeAccountCopyConverter(string? home = null) : ISessionConverter
{
    private readonly string _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public SessionMigrationKind Kind => SessionMigrationKind.Native;

    public bool CanConvert(string sourceAgentId, string targetAgentId) =>
        string.Equals(sourceAgentId, "claude", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(targetAgentId, "claude", StringComparison.OrdinalIgnoreCase);

    public Task<SessionMigrationResult> ConvertAsync(SessionTranscript transcript, SessionMigrationRequest request, CancellationToken cancellationToken = default)
    {
        var session = transcript.Session;
        var targetAccount = request.TargetAccountDirectory ?? Path.Combine(_home, ".claude");
        if (SamePath(session.AccountDirectory, targetAccount))
        {
            throw new InvalidOperationException("The session already lives in this account.");
        }

        var folder = ClaudeProjectFolder.For(targetAccount, request.ProjectPath);
        Directory.CreateDirectory(folder);
        var id = session.SessionId;
        var target = Path.Combine(folder, id + ".jsonl");

        if (File.Exists(target) && !SameContent(session.FilePath, target))
        {
            // Another session already owns this id there: keep both by giving the copy its own id.
            id = Guid.NewGuid().ToString();
            target = Path.Combine(folder, id + ".jsonl");
            CopyRewritten(session.FilePath, target, id, cancellationToken);
        }
        else if (!File.Exists(target))
        {
            CopyPlain(session.FilePath, target);
        }

        var sibling = Path.Combine(Path.GetDirectoryName(session.FilePath) ?? "", session.SessionId);
        if (Directory.Exists(sibling)) CopyDirectory(sibling, Path.Combine(folder, id), cancellationToken);

        return Task.FromResult(new SessionMigrationResult(SessionMigrationKind.Native, ResumeSessionId: id));
    }

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd('/', '\\');
        return string.Equals(Norm(a), Norm(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>Temp file plus move, so a half-written transcript is never resumed.</summary>
    private static void CopyPlain(string source, string target)
    {
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temp, true);
            File.Move(temp, target, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void CopyRewritten(string source, string target, string newId, CancellationToken cancellationToken)
    {
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new StreamWriter(temp, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                foreach (var line in File.ReadLines(source))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.WriteLine(Rewrite(line, newId));
                }
            }
            File.Move(temp, target, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string Rewrite(string line, string newId)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return line;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Name == "sessionId" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteString("sessionId", newId);
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return line;
        }
    }

    /// <summary>Copies what is missing; files already there are left alone.</summary>
    private static void CopyDirectory(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination)) CopyPlain(file, destination);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), cancellationToken);
        }
    }
}
