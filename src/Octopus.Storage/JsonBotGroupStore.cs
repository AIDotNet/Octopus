using System.Text.Json;
using Octopus.Core.Bots;
using Octopus.Platform;

namespace Octopus.Storage;

/// <summary>
/// Persists Bot Group definitions in <c>&lt;DataDirectory&gt;/bot-groups.json</c> and per-group message transcripts
/// and pending deliveries in <c>&lt;DataDirectory&gt;/bot-groups/&lt;groupId&gt;.json</c>.
/// </summary>
public sealed class JsonBotGroupStore(IAppPaths paths) : IBotGroupStore
{
    private readonly string _indexPath = Path.Combine(paths.DataDirectory, "bot-groups.json");
    private readonly string _roomsDir = Path.Combine(paths.DataDirectory, "bot-groups");
    private readonly object _gate = new();
    private readonly Dictionary<string, BotGroupRoomDocument> _rooms = new(StringComparer.Ordinal);
    private bool _loaded;

    public BotGroupsDocument Current
    {
        get
        {
            EnsureLoaded();
            return field;
        }
        private set;
    } = new();

    public Exception? LastError { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        lock (_gate)
        {
            Current = ReadIndex();
            _rooms.Clear();
            _loaded = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<BotGroupsDocument> mutate)
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                Current = ReadIndex();
                _loaded = true;
            }
            mutate(Current);
            Current = Normalize(Current);
            try
            {
                var text = JsonSerializer.Serialize(Current, OctopusJsonContext.FileTypeInfo<BotGroupsDocument>());
                AtomicFile.WriteAllText(_indexPath, text);
                LastError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = e;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public BotGroupRoomDocument ReadRoom(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return new BotGroupRoomDocument();
        lock (_gate)
        {
            return GetOrLoadRoomLocked(groupId);
        }
    }

    public BotGroupRoomDocument UpdateRoom(string groupId, Action<BotGroupRoomDocument> mutate)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return new BotGroupRoomDocument();
        BotGroupRoomDocument room;
        lock (_gate)
        {
            room = GetOrLoadRoomLocked(groupId);
            mutate(room);
            room = NormalizeRoom(room, groupId);
            _rooms[groupId] = room;
            try
            {
                Directory.CreateDirectory(_roomsDir);
                var text = JsonSerializer.Serialize(room, OctopusJsonContext.FileTypeInfo<BotGroupRoomDocument>());
                AtomicFile.WriteAllText(RoomPath(groupId), text);
                LastError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = e;
            }
        }
        return room;
    }

    public void DeleteRoom(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        lock (_gate)
        {
            _rooms.Remove(groupId);
            try
            {
                var path = RoomPath(groupId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LastError = e;
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            Current = ReadIndex();
            _loaded = true;
        }
    }

    private BotGroupsDocument ReadIndex()
    {
        if (!File.Exists(_indexPath) || !AtomicFile.TryReadAllText(_indexPath, out var text)) return new BotGroupsDocument();
        try
        {
            return Normalize(JsonSerializer.Deserialize(text, OctopusJsonContext.FileTypeInfo<BotGroupsDocument>()));
        }
        catch (JsonException)
        {
            AtomicFile.Quarantine(_indexPath);
            return new BotGroupsDocument();
        }
    }

    private BotGroupRoomDocument GetOrLoadRoomLocked(string groupId)
    {
        if (_rooms.TryGetValue(groupId, out var cached)) return cached;
        var path = RoomPath(groupId);
        BotGroupRoomDocument room;
        if (!File.Exists(path) || !AtomicFile.TryReadAllText(path, out var text))
        {
            room = NormalizeRoom(null, groupId);
        }
        else
        {
            try
            {
                room = NormalizeRoom(JsonSerializer.Deserialize(text, OctopusJsonContext.FileTypeInfo<BotGroupRoomDocument>()), groupId);
            }
            catch (JsonException)
            {
                AtomicFile.Quarantine(path);
                room = NormalizeRoom(null, groupId);
            }
        }
        _rooms[groupId] = room;
        return room;
    }

    private string RoomPath(string groupId)
    {
        var safe = JsonBotStore.SanitizeSlug(groupId, "group");
        return Path.Combine(_roomsDir, $"{safe}.json");
    }

    public static BotGroupsDocument Normalize(BotGroupsDocument? document)
    {
        document ??= new BotGroupsDocument();
        document.Version = Math.Max(1, document.Version);
        document.Groups ??= [];

        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        document.Groups.RemoveAll(g => g is null || string.IsNullOrWhiteSpace(g.Id) || !seenGroups.Add(g.Id.Trim()));

        foreach (var group in document.Groups)
        {
            group.Id = group.Id.Trim();
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? "Bot Group" : group.Name.Trim();
            group.HostDeviceKey = string.IsNullOrWhiteSpace(group.HostDeviceKey) ? "local" : group.HostDeviceKey.Trim();
            group.Revision = Math.Max(1, group.Revision);
            group.MaxHops = Math.Clamp(group.MaxHops <= 0 ? BotGroupDefinition.DefaultMaxHops : group.MaxHops, 1, 30);
            group.ReplyMode ??= new BotGroupReplyMode();
            group.Members ??= [];
            group.FormerChatIds ??= [];
            group.Threads ??= [];

            NormalizeMembers(group.Members);
            group.FormerChatIds = group.FormerChatIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var seenThreads = new HashSet<string>(StringComparer.Ordinal);
            group.Threads.RemoveAll(t => t is null || string.IsNullOrWhiteSpace(t.Id) || !seenThreads.Add(t.Id.Trim()));
            foreach (var thread in group.Threads)
            {
                thread.Id = thread.Id.Trim();
                thread.Members ??= [];
                thread.FormerChatIds ??= [];
                NormalizeMembers(thread.Members);
                thread.FormerChatIds = thread.FormerChatIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (!string.IsNullOrWhiteSpace(thread.Title) && thread.Title.Length > 80)
                {
                    thread.Title = thread.Title[..80];
                }
            }

            if (group.ReplyMode.Kind == BotGroupReplyModeKind.Bot
                && (string.IsNullOrWhiteSpace(group.ReplyMode.BotId)
                    || group.Members.All(m => m.BotId != group.ReplyMode.BotId)))
            {
                group.ReplyMode = new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Mentions };
            }
        }

        return document;
    }

    public static BotGroupRoomDocument NormalizeRoom(BotGroupRoomDocument? room, string groupId)
    {
        room ??= new BotGroupRoomDocument();
        room.Version = Math.Max(1, room.Version);
        room.GroupId = groupId.Trim();
        room.Messages ??= [];
        room.PendingDeliveries ??= [];

        var seenMessages = new HashSet<string>(StringComparer.Ordinal);
        room.Messages.RemoveAll(m => m is null || string.IsNullOrWhiteSpace(m.Id) || !seenMessages.Add(m.Id.Trim()));
        long maxSeq = 0;
        foreach (var msg in room.Messages)
        {
            msg.Id = msg.Id.Trim();
            msg.GroupId = room.GroupId;
            msg.Turns ??= [];
            var seenOps = new HashSet<string>(StringComparer.Ordinal);
            msg.Turns.RemoveAll(t => t is null || string.IsNullOrWhiteSpace(t.OperationId) || !seenOps.Add(t.OperationId.Trim()));
            if (msg.Sequence > maxSeq) maxSeq = msg.Sequence;
        }
        room.Messages.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        room.NextSequence = Math.Max(room.NextSequence, maxSeq + 1);

        var seenPending = new HashSet<string>(StringComparer.Ordinal);
        room.PendingDeliveries.RemoveAll(p =>
            p is null
            || string.IsNullOrWhiteSpace(p.OperationId)
            || !seenPending.Add(p.OperationId.Trim()));

        return room;
    }

    private static void NormalizeMembers(List<BotGroupMember> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        members.RemoveAll(m =>
            m is null
            || string.IsNullOrWhiteSpace(m.BotId)
            || string.IsNullOrWhiteSpace(m.ChatId)
            || !seen.Add(m.BotId.Trim()));
        foreach (var m in members)
        {
            m.BotId = m.BotId.Trim();
            m.ChatId = m.ChatId.Trim();
            m.StoppedThroughSequence = Math.Max(0, m.StoppedThroughSequence);
        }
    }
}
