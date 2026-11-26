using System.Text.Json;
using Octopus.Core.Bots;
using Octopus.Platform;

namespace Octopus.Storage;

/// <summary>Stores Bot definitions, chats, connectors, and work items in <c>&lt;DataDirectory&gt;/bots.json</c>.</summary>
public sealed class JsonBotStore(IAppPaths paths) : IBotStore
{
    private readonly string _path = Path.Combine(paths.DataDirectory, "bots.json");
    private readonly object _gate = new();
    private bool _loaded;

    public BotsDocument Current
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
            Current = Read();
            _loaded = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<BotsDocument> mutate)
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                Current = Read();
                _loaded = true;
            }
            mutate(Current);
            Current = Normalize(Current);
            try
            {
                var text = JsonSerializer.Serialize(Current, OctopusJsonContext.FileTypeInfo<BotsDocument>());
                AtomicFile.WriteAllText(_path, text);
                LastError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = e;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            Current = Read();
            _loaded = true;
        }
    }

    private BotsDocument Read()
    {
        if (!File.Exists(_path) || !AtomicFile.TryReadAllText(_path, out var text)) return new BotsDocument();
        try
        {
            return Normalize(JsonSerializer.Deserialize(text, OctopusJsonContext.FileTypeInfo<BotsDocument>()));
        }
        catch (JsonException)
        {
            AtomicFile.Quarantine(_path);
            return new BotsDocument();
        }
    }

    public static BotsDocument Normalize(BotsDocument? document)
    {
        document ??= new BotsDocument();
        document.Version = Math.Max(1, document.Version);
        document.Bots ??= [];
        var seenBots = new HashSet<string>(StringComparer.Ordinal);
        document.Bots.RemoveAll(b => b is null || string.IsNullOrWhiteSpace(b.Id) || !seenBots.Add(b.Id.Trim()));

        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bot in document.Bots)
        {
            bot.Id = bot.Id.Trim();
            bot.Name = string.IsNullOrWhiteSpace(bot.Name) ? "Bot" : bot.Name.Trim();
            bot.Slug = MakeUniqueSlug(bot.Slug, bot.Name, usedSlugs);
            bot.AgentId = string.IsNullOrWhiteSpace(bot.AgentId) ? "claude" : bot.AgentId.Trim();
            bot.Color = string.IsNullOrWhiteSpace(bot.Color) ? BotCatalog.AvatarPalette[0] : bot.Color.Trim();
            bot.Chats ??= [];
            bot.Connectors ??= [];
            bot.Items ??= [];

            var seenChats = new HashSet<string>(StringComparer.Ordinal);
            bot.Chats.RemoveAll(c => c is null || string.IsNullOrWhiteSpace(c.Id) || !seenChats.Add(c.Id.Trim()));
            foreach (var chat in bot.Chats)
            {
                chat.Id = chat.Id.Trim();
                chat.Title = string.IsNullOrWhiteSpace(chat.Title)
                    ? (chat.Kind == BotChatKind.Home ? "Home Chat" : "Chat")
                    : chat.Title.Trim();
                if (chat.LastActivityAt == default) chat.LastActivityAt = chat.CreatedAt == default ? DateTimeOffset.UtcNow : chat.CreatedAt;
            }
            if (bot.HomeChatId is not null && bot.Chats.All(c => c.Id != bot.HomeChatId))
            {
                bot.HomeChatId = bot.Chats.FirstOrDefault(c => c.Kind == BotChatKind.Home)?.Id;
            }
            if (bot.ActiveChatId is not null && bot.Chats.All(c => c.Id != bot.ActiveChatId))
            {
                bot.ActiveChatId = bot.HomeChatId ?? bot.Chats.FirstOrDefault()?.Id;
            }

            var seenConns = new HashSet<string>(StringComparer.Ordinal);
            bot.Connectors.RemoveAll(c => c is null || string.IsNullOrWhiteSpace(c.Id) || !seenConns.Add(c.Id.Trim()));
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            bot.Items.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Id) || !seenItems.Add(i.Id.Trim()));
            foreach (var item in bot.Items)
            {
                item.Options ??= [];
            }
        }

        if (document.ActiveBotId is not null && document.Bots.All(b => b.Id != document.ActiveBotId))
        {
            document.ActiveBotId = document.Bots.FirstOrDefault()?.Id;
        }
        if (document.ActiveBotId is null && document.Bots.Count > 0)
        {
            document.ActiveBotId = document.Bots[0].Id;
        }

        return document;
    }

    public static string SanitizeSlug(string? raw, string? fallbackName = null)
    {
        var source = string.IsNullOrWhiteSpace(raw) ? fallbackName ?? "" : raw;
        var chars = new List<char>(source.Length);
        var lastDash = false;
        foreach (var ch in source.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                chars.Add(ch);
                lastDash = false;
            }
            else if ((ch is '-' or '_' or ' ') && chars.Count > 0 && !lastDash)
            {
                chars.Add('-');
                lastDash = true;
            }
        }
        while (chars.Count > 0 && chars[^1] == '-') chars.RemoveAt(chars.Count - 1);
        return chars.Count > 0 ? new string(chars.ToArray()) : "bot";
    }

    private static string MakeUniqueSlug(string? raw, string fallbackName, HashSet<string> used)
    {
        var baseSlug = SanitizeSlug(raw, fallbackName);
        var candidate = baseSlug;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseSlug}-{suffix++}";
        }
        return candidate;
    }
}
