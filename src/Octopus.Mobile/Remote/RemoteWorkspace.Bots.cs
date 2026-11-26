using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;

namespace Octopus.Mobile.Remote;

internal sealed partial class RemoteWorkspace
{
    public bool SupportsBots(RemoteHost host) =>
        _connections.ClientOf(host.Id) is { SupportsBots: true };

    public async Task<RemoteBot> CreateBotAsync(RemoteHost host, CreateRemoteBotRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(host.Id) ?? await _connections.ConnectAsync(host.Id).ConfigureAwait(false);
            if (!client.SupportsBots) throw new IOException("This computer needs an update to support Bots.");
            var created = await client.CreateBotAsync(new CreateBotMessage
            {
                Name = request.Name,
                AgentId = request.AgentId,
                Color = request.Color,
                TemplateId = request.TemplateId,
                Soul = request.Soul,
            }, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() => UpsertBot(host, created)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBot> ImportBotAsync(RemoteHost host, string shareLink, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(host.Id) ?? await _connections.ConnectAsync(host.Id).ConfigureAwait(false);
            if (!client.SupportsBots) throw new IOException("This computer needs an update to support Bots.");
            var imported = await client.ImportBotAsync(shareLink, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() => UpsertBot(host, imported)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBot> UpdateBotAsync(
        RemoteBot bot,
        bool? isPaused = null,
        bool? lookAroundEnabled = null,
        bool? tidyUpMemoryEnabled = null,
        string? agentId = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(bot.Host.Id) ?? await _connections.ConnectAsync(bot.Host.Id).ConfigureAwait(false);
            var updated = await client.UpdateBotAsync(new UpdateBotMessage
            {
                BotId = bot.Id,
                Name = name,
                AgentId = agentId,
                IsPaused = isPaused,
                LookAroundEnabled = lookAroundEnabled,
                TidyUpMemoryEnabled = tidyUpMemoryEnabled,
            }, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() => UpsertBot(bot.Host, updated)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task DeleteBotAsync(RemoteBot bot, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(bot.Host.Id) ?? await _connections.ConnectAsync(bot.Host.Id).ConfigureAwait(false);
            await client.DeleteBotAsync(bot.Id, cancellationToken).ConfigureAwait(false);
            await UiAsync(() => bot.Host.Bots.Remove(bot)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBotChat> CreateBotChatAsync(RemoteBot bot, CreateRemoteBotChatRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(bot.Host.Id) ?? await _connections.ConnectAsync(bot.Host.Id).ConfigureAwait(false);
            var created = await client.CreateBotChatAsync(new CreateBotChatMessage
            {
                BotId = bot.Id,
                AgentId = request.AgentId,
                Title = request.Title,
            }, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() => UpsertBotChat(bot, created, insertFirstNonHome: true)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBotChat> OpenBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(chat.Bot.Host.Id) ?? await _connections.ConnectAsync(chat.Bot.Host.Id).ConfigureAwait(false);
            var opened = await client.OpenBotChatAsync(chat.Bot.Id, chat.Id, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() => UpsertBotChat(chat.Bot, opened)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task CloseBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(chat.Bot.Host.Id) ?? await _connections.ConnectAsync(chat.Bot.Host.Id).ConfigureAwait(false);
            await client.CloseBotChatAsync(chat.Bot.Id, chat.Id, cancellationToken).ConfigureAwait(false);
            await UiAsync(() =>
            {
                chat.IsOpen = false;
                chat.Activity = SessionActivity.Idle;
                chat.ClosedAt = DateTimeOffset.Now;
            }).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task RenameBotChatAsync(RemoteBotChat chat, string? title, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(chat.Bot.Host.Id) ?? await _connections.ConnectAsync(chat.Bot.Host.Id).ConfigureAwait(false);
            await client.RenameBotChatAsync(chat.Bot.Id, chat.Id, title, cancellationToken).ConfigureAwait(false);
            await UiAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(title)) chat.Title = title.Trim();
            }).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task DeleteBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(chat.Bot.Host.Id) ?? await _connections.ConnectAsync(chat.Bot.Host.Id).ConfigureAwait(false);
            await client.DeleteBotChatAsync(chat.Bot.Id, chat.Id, cancellationToken).ConfigureAwait(false);
            await UiAsync(() => chat.Bot.Chats.Remove(chat)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task ResolveBotWorkItemAsync(RemoteBot bot, RemoteBotWorkItem item, string? chosenOption = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(bot.Host.Id) ?? await _connections.ConnectAsync(bot.Host.Id).ConfigureAwait(false);
            await client.ResolveBotItemAsync(bot.Id, item.Id, chosenOption, cancellationToken).ConfigureAwait(false);
            await UiAsync(() =>
            {
                item.Status = "done";
                if (!string.IsNullOrWhiteSpace(chosenOption)) item.Meta = chosenOption;
            }).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task ToggleBotConnectorAsync(RemoteBot bot, RemoteBotConnector connector, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(bot.Host.Id) ?? await _connections.ConnectAsync(bot.Host.Id).ConfigureAwait(false);
            await client.ToggleBotConnectorAsync(bot.Id, connector.Id, enabled, cancellationToken).ConfigureAwait(false);
            await UiAsync(() => connector.Enabled = enabled).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    private void ApplyBots(RemoteHost host, IReadOnlyList<BotMessage> botMessages)
    {
        var target = new List<RemoteBot>(botMessages.Count);
        foreach (var msg in botMessages)
        {
            var bot = host.Bots.FirstOrDefault(known => known.Id == msg.Id)
                ?? new RemoteBot(host, msg.Id, msg.Name, msg.HomePath);
            SyncBotFields(bot, msg);
            target.Add(bot);
        }
        Replace(host.Bots, target);
    }

    private static RemoteBot UpsertBot(RemoteHost host, BotMessage msg)
    {
        var existing = host.Bots.FirstOrDefault(b => b.Id == msg.Id);
        var bot = existing ?? new RemoteBot(host, msg.Id, msg.Name, msg.HomePath);
        SyncBotFields(bot, msg);
        if (existing is null)
        {
            host.Bots.Insert(0, bot);
        }
        return bot;
    }

    private static void SyncBotFields(RemoteBot bot, BotMessage msg)
    {
        bot.Name = msg.Name;
        bot.Slug = msg.Slug;
        bot.AgentId = msg.AgentId;
        bot.Color = string.IsNullOrWhiteSpace(msg.Color) ? "#5b5bd6" : msg.Color;
        bot.Icon = msg.Icon;
        bot.SoulSummary = msg.SoulSummary;
        bot.HomePath = msg.HomePath;
        bot.IsPaused = msg.IsPaused;
        bot.LookAroundEnabled = msg.LookAroundEnabled;
        bot.LookAroundIntervalHours = msg.LookAroundIntervalHours;
        bot.TidyUpMemoryEnabled = msg.TidyUpMemoryEnabled;
        bot.StaleAfterDays = msg.StaleAfterDays;
        bot.HomeChatId = msg.HomeChatId;
        bot.ShareLink = msg.ShareLink;

        var chats = msg.Chats.Select(c =>
        {
            var chat = bot.Chats.FirstOrDefault(known => known.Id == c.Id)
                ?? new RemoteBotChat(bot, c.Id, c.Kind, c.Title);
            SyncBotChatFields(chat, c);
            return chat;
        }).ToList();
        Replace(bot.Chats, chats);

        var conns = msg.Connectors.Select(c =>
        {
            var conn = bot.Connectors.FirstOrDefault(known => known.Id == c.Id)
                ?? new RemoteBotConnector(bot, c.Id, c.AppId, c.DisplayName);
            conn.Url = c.Url;
            conn.Transport = c.Transport;
            conn.AuthMode = c.AuthMode;
            conn.HasSecret = c.HasSecret;
            conn.Enabled = c.Enabled;
            return conn;
        }).ToList();
        Replace(bot.Connectors, conns);

        var items = msg.Items.Select(i =>
        {
            var item = bot.Items.FirstOrDefault(known => known.Id == i.Id)
                ?? new RemoteBotWorkItem(bot, i.Id, i.Number, i.Title);
            item.Title = i.Title;
            item.Status = i.Status;
            item.Question = i.Question;
            item.Options = i.Options;
            item.ChatId = i.ChatId;
            item.Meta = i.Meta;
            return item;
        }).ToList();
        Replace(bot.Items, items);
    }

    private static RemoteBotChat UpsertBotChat(RemoteBot bot, BotChatMessage msg, bool insertFirstNonHome = false)
    {
        var existing = bot.Chats.FirstOrDefault(c => c.Id == msg.Id);
        var chat = existing ?? new RemoteBotChat(bot, msg.Id, msg.Kind, msg.Title);
        SyncBotChatFields(chat, msg);
        if (existing is null)
        {
            if (insertFirstNonHome)
            {
                var idx = bot.Chats.ToList().FindIndex(c => !c.IsHome);
                if (idx < 0) bot.Chats.Add(chat);
                else bot.Chats.Insert(idx, chat);
            }
            else
            {
                bot.Chats.Add(chat);
            }
        }
        return chat;
    }

    private static void SyncBotChatFields(RemoteBotChat chat, BotChatMessage msg)
    {
        chat.Title = msg.Title;
        chat.AgentId = msg.AgentId;
        chat.Subtitle = msg.Subtitle;
        chat.Activity = ActivityOf(msg.Activity);
        chat.IsGui = msg.Surface == ItemSurfaces.Gui;
        chat.IsOpen = msg.IsOpen;
        chat.LastActivityAt = msg.LastActivityAt;
        chat.ClosedAt = msg.ClosedAt;
    }
}
