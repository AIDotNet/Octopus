using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Bots;
using Octopus.Core.Projects;
using Octopus.Storage;

namespace Octopus.App.Bots;

/// <summary>In-memory coordinator for Bots, persisted via <see cref="IBotStore"/> and backed by <see cref="BotHomeManager"/>.</summary>
public sealed class BotService : IBotService
{
    public const string BackingProjectPrefix = "bot:";

    private readonly IBotStore _store;
    private readonly IProjectService _projects;
    private readonly IAgentCatalog _catalog;
    private readonly IAgentScanner _scanner;
    private readonly ISettingsStore _settings;
    private readonly PathComparer _paths;
    private readonly IItemActivityService? _activity;
    private readonly Dictionary<string, Project> _backingProjects = new(StringComparer.Ordinal);
    private bool _loaded;

    public BotService(
        IBotStore store,
        IProjectService projects,
        IAgentCatalog catalog,
        IAgentScanner scanner,
        ISettingsStore settings,
        PathComparer paths,
        BotHomeManager home,
        IItemActivityService? activity = null)
    {
        _store = store;
        _projects = projects;
        _catalog = catalog;
        _scanner = scanner;
        _settings = settings;
        _paths = paths;
        Home = home;
        _activity = activity;

        // Clicking a project in the rail switches out of Bot mode back to Project mode.
        _projects.Changed += OnProjectsChanged;
    }

    public BotHomeManager Home { get; }

    public IReadOnlyList<BotDefinition> Bots
    {
        get
        {
            EnsureLoaded();
            return _store.Current.Bots;
        }
    }

    public bool IsBotMode
    {
        get
        {
            EnsureLoaded();
            return _store.Current.IsBotMode;
        }
    }

    public BotDefinition? CurrentBot
    {
        get
        {
            EnsureLoaded();
            var doc = _store.Current;
            return doc.Bots.FirstOrDefault(b => b.Id == doc.ActiveBotId) ?? doc.Bots.FirstOrDefault();
        }
    }

    public BotChat? CurrentChat
    {
        get
        {
            var bot = CurrentBot;
            if (bot is null) return null;
            return bot.Chats.FirstOrDefault(c => c.Id == bot.ActiveChatId)
                ?? bot.Chats.FirstOrDefault(c => c.Id == bot.HomeChatId)
                ?? bot.Chats.FirstOrDefault();
        }
    }

    public event EventHandler<BotChangedEventArgs>? Changed;

    public void Load()
    {
        _store.Load();
        _loaded = true;
        foreach (var bot in _store.Current.Bots)
        {
            Home.EnsureHome(bot);
            EnsureBackingProject(bot);
        }
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Bots));
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var bot in _store.Current.Bots)
        {
            EnsureBackingProject(bot);
        }
    }

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.CurrentProject or ProjectChangeKind.Device && IsBotMode)
        {
            SetBotMode(false);
        }
    }

    public void SetBotMode(bool enabled)
    {
        EnsureLoaded();
        if (_store.Current.IsBotMode == enabled) return;
        _store.Update(d => d.IsBotMode = enabled);
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Mode, CurrentBot, CurrentChat));
    }

    public void ToggleBotMode() => SetBotMode(!IsBotMode);

    public BotDefinition CreateBot(NewBotRequest request)
    {
        EnsureLoaded();
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? AppLangResources.Get(AppLangResourceKind.BotDefaultName)
            : request.Name.Trim();
        var agentId = ResolveAgentId(request.AgentId);
        var color = string.IsNullOrWhiteSpace(request.Color)
            ? BotCatalog.AvatarPalette[Bots.Count % BotCatalog.AvatarPalette.Count]
            : request.Color.Trim();

        var botId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var homeChat = new BotChat
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = BotChatKind.Home,
            Title = "Home Chat",
            AgentId = agentId,
            Subtitle = AppLangResources.Format(AppLangResourceKind.BotHomeChatSubtitle, name),
            Surface = ResolveSurface(agentId),
            Status = BotChatStatus.Idle,
            IsOpen = true,
            CreatedAt = now,
            LastActivityAt = now,
        };

        var bot = new BotDefinition
        {
            Id = botId,
            Name = name,
            Slug = JsonBotStore.SanitizeSlug(null, name),
            AgentId = agentId,
            Color = color,
            TemplateId = request.TemplateId,
            Soul = string.IsNullOrWhiteSpace(request.Soul) ? null : request.Soul.Trim(),
            IsPaused = false,
            IsExpanded = true,
            HomeChatId = homeChat.Id,
            ActiveChatId = homeChat.Id,
            Chats = [homeChat],
            CreatedAt = now,
        };

        _store.Update(d =>
        {
            d.Bots.Insert(0, bot);
            d.ActiveBotId = bot.Id;
            d.ActiveChatId = homeChat.Id;
            d.IsBotMode = true;
        });

        var persisted = FindBot(botId)!;
        Home.EnsureHome(persisted);
        EnsureBackingProject(persisted);

        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Bots, persisted, homeChat));
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Mode, persisted, homeChat));
        return persisted;
    }

    public void SelectBot(string botId)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        if (bot is null) return;
        var modeChanged = !IsBotMode;
        var botChanged = _store.Current.ActiveBotId != bot.Id;
        var activeChat = bot.Chats.FirstOrDefault(c => c.Id == bot.ActiveChatId)
            ?? bot.Chats.FirstOrDefault(c => c.Id == bot.HomeChatId)
            ?? bot.Chats.FirstOrDefault();

        _store.Update(d =>
        {
            d.IsBotMode = true;
            d.ActiveBotId = bot.Id;
            d.ActiveChatId = activeChat?.Id;
        });
        EnsureBackingProject(bot);

        if (modeChanged) Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Mode, bot, activeChat));
        if (botChanged || modeChanged) Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.CurrentBot, bot, activeChat));
    }

    public void SetBotExpanded(string botId, bool expanded)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        if (bot is null || bot.IsExpanded == expanded) return;
        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
            {
                target.IsExpanded = expanded;
            }
        });
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Bots, FindBot(botId)));
    }

    public void RenameBot(string botId, string name)
    {
        EnsureLoaded();
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || FindBot(botId) is null) return;
        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
            {
                target.Name = trimmed;
            }
        });
        var updated = FindBot(botId)!;
        Home.SyncManagedFiles(updated);
        if (_backingProjects.TryGetValue(updated.Id, out var proj))
        {
            proj.Name = updated.Name;
        }
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Bots, updated));
    }

    public void SetBotPaused(string botId, bool paused)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        if (bot is null || bot.IsPaused == paused) return;
        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
            {
                target.IsPaused = paused;
            }
        });
        var updated = FindBot(botId)!;
        Home.SyncManagedFiles(updated);
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Bots, updated));
    }

    public void DeleteBot(string botId)
    {
        EnsureLoaded();
        var removed = FindBot(botId);
        if (removed is null) return;
        _backingProjects.Remove(botId);
        _store.Update(d =>
        {
            d.Bots.RemoveAll(b => b.Id == botId);
            if (d.ActiveBotId == botId)
            {
                d.ActiveBotId = d.Bots.FirstOrDefault()?.Id;
                d.ActiveChatId = d.Bots.FirstOrDefault()?.ActiveChatId;
            }
        });
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Bots, removed));
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.CurrentBot, CurrentBot, CurrentChat));
    }

    public BotChat? CreateChat(string botId, string? agentId = null, string? title = null, bool activate = true)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        if (bot is null) return null;

        var resolvedAgentId = ResolveAgentId(agentId ?? bot.AgentId);
        var agentName = _catalog.Find(resolvedAgentId)?.Name ?? resolvedAgentId;
        var chatTitle = string.IsNullOrWhiteSpace(title) ? $"{agentName} Chat" : title.Trim();
        var now = DateTimeOffset.UtcNow;
        var chat = new BotChat
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = BotChatKind.Chat,
            Title = chatTitle,
            AgentId = resolvedAgentId,
            Surface = ResolveSurface(resolvedAgentId),
            Status = BotChatStatus.Idle,
            IsOpen = true,
            CreatedAt = now,
            LastActivityAt = now,
        };

        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is not { } target) return;
            var insertAt = target.Chats.FindIndex(c => c.Kind != BotChatKind.Home);
            if (insertAt < 0) target.Chats.Add(chat);
            else target.Chats.Insert(insertAt, chat);
            target.IsExpanded = true;
            if (activate)
            {
                target.ActiveChatId = chat.Id;
                d.ActiveBotId = target.Id;
                d.ActiveChatId = chat.Id;
                d.IsBotMode = true;
            }
        });

        var updated = FindBot(botId)!;
        var backing = EnsureBackingProject(updated);
        var backingItem = ToProjectItem(updated, chat);
        if (backing.Items.All(i => i.Id != backingItem.Id))
        {
            backing.Items.Add(backingItem);
        }
        if (activate)
        {
            backing.ActiveItemId = backingItem.Id;
        }

        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Chats, updated, chat, backingItem));
        if (activate)
        {
            Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.ActiveChat, updated, chat, backingItem));
        }
        return chat;
    }

    public void OpenChat(string botId, string chatId)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        var chat = bot?.Chats.FirstOrDefault(c => c.Id == chatId);
        if (bot is null || chat is null) return;

        var wasClosed = !chat.IsOpen || chat.Status == BotChatStatus.Closed;
        var modeChanged = !IsBotMode;
        var botChanged = _store.Current.ActiveBotId != bot.Id;

        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is not { } target) return;
            if (target.Chats.FirstOrDefault(c => c.Id == chatId) is not { } targetChat) return;
            targetChat.IsOpen = true;
            if (targetChat.Status == BotChatStatus.Closed) targetChat.Status = BotChatStatus.Idle;
            targetChat.ClosedAt = null;
            targetChat.LastActivityAt = DateTimeOffset.UtcNow;
            target.ActiveChatId = targetChat.Id;
            d.ActiveBotId = target.Id;
            d.ActiveChatId = targetChat.Id;
            d.IsBotMode = true;
        });

        var updated = FindBot(botId)!;
        var updatedChat = updated.Chats.First(c => c.Id == chatId);
        var backing = EnsureBackingProject(updated);
        var backingItem = backing.Items.FirstOrDefault(i => i.Id == chatId);
        if (backingItem is null)
        {
            backingItem = ToProjectItem(updated, updatedChat);
            backing.Items.Add(backingItem);
        }
        backing.ActiveItemId = backingItem.Id;

        if (modeChanged) Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Mode, updated, updatedChat, backingItem));
        if (botChanged) Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.CurrentBot, updated, updatedChat, backingItem));
        if (wasClosed) Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Chats, updated, updatedChat, backingItem));
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.ActiveChat, updated, updatedChat, backingItem));
    }

    public void CloseChat(string botId, string chatId)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        var chat = bot?.Chats.FirstOrDefault(c => c.Id == chatId);
        if (bot is null || chat is null) return;

        ProjectItem? removedItem = null;
        if (_backingProjects.TryGetValue(botId, out var backing))
        {
            removedItem = backing.Items.FirstOrDefault(i => i.Id == chatId);
            if (removedItem is not null) backing.Items.Remove(removedItem);
        }

        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is not { } target) return;
            if (target.Chats.FirstOrDefault(c => c.Id == chatId) is not { } targetChat) return;
            targetChat.IsOpen = false;
            targetChat.Status = BotChatStatus.Closed;
            targetChat.ClosedAt = DateTimeOffset.UtcNow;
            if (target.ActiveChatId == chatId)
            {
                target.ActiveChatId = target.Chats.FirstOrDefault(c => c.IsOpen)?.Id;
                if (d.ActiveBotId == botId) d.ActiveChatId = target.ActiveChatId;
            }
        });

        var updated = FindBot(botId)!;
        if (_backingProjects.TryGetValue(botId, out backing))
        {
            backing.ActiveItemId = updated.ActiveChatId ?? backing.Items.FirstOrDefault()?.Id;
        }
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Chats, updated, updated.Chats.FirstOrDefault(c => c.Id == chatId), removedItem));
    }

    public void RenameChat(string botId, string chatId, string? customTitle)
    {
        EnsureLoaded();
        var normalized = string.IsNullOrWhiteSpace(customTitle) ? null : customTitle.Trim();
        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId)?.Chats.FirstOrDefault(c => c.Id == chatId) is { } targetChat)
            {
                targetChat.CustomTitle = normalized;
            }
        });
        if (_backingProjects.TryGetValue(botId, out var backing)
            && backing.Items.FirstOrDefault(i => i.Id == chatId) is { } item)
        {
            item.CustomTitle = normalized;
        }
        var updated = FindBot(botId);
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Chats, updated, updated?.Chats.FirstOrDefault(c => c.Id == chatId)));
    }

    public void DeleteChat(string botId, string chatId)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        var chat = bot?.Chats.FirstOrDefault(c => c.Id == chatId);
        if (bot is null || chat is null || chat.Kind == BotChatKind.Home) return;

        ProjectItem? removedItem = null;
        if (_backingProjects.TryGetValue(botId, out var backing))
        {
            removedItem = backing.Items.FirstOrDefault(i => i.Id == chatId);
            if (removedItem is not null) backing.Items.Remove(removedItem);
        }

        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is not { } target) return;
            target.Chats.RemoveAll(c => c.Id == chatId);
            if (target.ActiveChatId == chatId)
            {
                target.ActiveChatId = target.HomeChatId ?? target.Chats.FirstOrDefault()?.Id;
                if (d.ActiveBotId == botId) d.ActiveChatId = target.ActiveChatId;
            }
        });

        var updated = FindBot(botId)!;
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Chats, updated, chat, removedItem));
    }

    public BotConnectorBinding? AddConnector(string botId, string appId)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        var app = BotCatalog.FindConnectorApp(appId);
        if (bot is null || app is null) return null;

        if (bot.Connectors.FirstOrDefault(c => string.Equals(c.AppId, app.Id, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            SetConnectorEnabled(botId, existing.Id, true);
            return existing;
        }

        var binding = new BotConnectorBinding
        {
            Id = Guid.NewGuid().ToString("N"),
            AppId = app.Id,
            DisplayName = app.Name,
            Category = app.CategoryId,
            Description = app.DescriptionZh,
            Enabled = true,
            StatusText = AppLangResources.Get(AppLangResourceKind.BotConnectorConnected),
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
            {
                target.Connectors.Add(binding);
            }
        });

        var updated = FindBot(botId)!;
        Home.SyncManagedFiles(updated);
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Connectors, updated));
        return binding;
    }

    public BotConnectorBinding? AddCustomUrlConnector(string botId, string name, string url)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        var trimmedUrl = url.Trim();
        if (bot is null || trimmedUrl.Length == 0) return null;

        var displayName = string.IsNullOrWhiteSpace(name) ? trimmedUrl : name.Trim();
        var binding = new BotConnectorBinding
        {
            Id = Guid.NewGuid().ToString("N"),
            AppId = "custom-url",
            DisplayName = displayName,
            Category = "custom",
            Description = trimmedUrl,
            Url = trimmedUrl,
            Enabled = true,
            StatusText = AppLangResources.Get(AppLangResourceKind.BotConnectorConnected),
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
            {
                target.Connectors.Add(binding);
            }
        });

        var updated = FindBot(botId)!;
        Home.SyncManagedFiles(updated);
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Connectors, updated));
        return binding;
    }

    public void SetConnectorEnabled(string botId, string connectorId, bool enabled)
    {
        EnsureLoaded();
        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId)?.Connectors.FirstOrDefault(c => c.Id == connectorId) is { } conn)
            {
                conn.Enabled = enabled;
            }
        });
        if (FindBot(botId) is { } updated)
        {
            Home.SyncManagedFiles(updated);
            Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Connectors, updated));
        }
    }

    public void RemoveConnector(string botId, string connectorId)
    {
        EnsureLoaded();
        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
            {
                target.Connectors.RemoveAll(c => c.Id == connectorId);
            }
        });
        if (FindBot(botId) is { } updated)
        {
            Home.SyncManagedFiles(updated);
            Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Connectors, updated));
        }
    }

    public BotWorkItem? AddWorkItem(
        string botId,
        string title,
        BotWorkItemStatus status = BotWorkItemStatus.Planned,
        string? question = null,
        IReadOnlyList<string>? options = null,
        string? chatId = null)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        if (bot is null || string.IsNullOrWhiteSpace(title)) return null;

        var now = DateTimeOffset.UtcNow;
        var nextNumber = bot.Items.Count > 0 ? bot.Items.Max(i => i.Number) + 1 : 1;
        var item = new BotWorkItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Number = nextNumber,
            Title = title.Trim(),
            Status = status,
            Question = question,
            Options = options is null ? [] : [.. options],
            ChatId = chatId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
            {
                target.Items.Insert(0, item);
            }
        });
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Items, FindBot(botId)));
        return item;
    }

    public void ResolveWorkItem(string botId, string itemId, string? chosenOption = null)
    {
        EnsureLoaded();
        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId)?.Items.FirstOrDefault(i => i.Id == itemId) is { } item)
            {
                item.Status = BotWorkItemStatus.Done;
                item.Meta = string.IsNullOrWhiteSpace(chosenOption) ? item.Meta : chosenOption;
                item.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Items, FindBot(botId)));
    }

    public ProjectItem? OpenFile(string botId, string filePath)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        if (bot is null || string.IsNullOrWhiteSpace(filePath)) return null;

        var backing = EnsureBackingProject(bot);
        var existing = backing.Items.FirstOrDefault(i => i.Kind == ProjectItemKind.File && _paths.Equals(i.FilePath, filePath));
        if (existing is not null)
        {
            backing.ActiveItemId = existing.Id;
            if (!IsBotMode || _store.Current.ActiveBotId != botId)
            {
                SelectBot(botId);
            }
            Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.ActiveChat, bot, Item: existing));
            return existing;
        }

        var item = new ProjectItem
        {
            Kind = ProjectItemKind.File,
            FilePath = filePath,
            Title = Path.GetFileName(filePath),
        };
        backing.Items.Add(item);
        backing.ActiveItemId = item.Id;

        if (!IsBotMode || _store.Current.ActiveBotId != botId)
        {
            SelectBot(botId);
        }
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Files, bot, Item: item));
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.ActiveChat, bot, Item: item));
        return item;
    }

    public Project GetBackingProject(BotDefinition bot)
    {
        EnsureLoaded();
        return EnsureBackingProject(bot);
    }

    public BotDefinition? FindBotByBackingProjectId(string projectId)
    {
        if (!projectId.StartsWith(BackingProjectPrefix, StringComparison.Ordinal)) return null;
        var botId = projectId[BackingProjectPrefix.Length..];
        return FindBot(botId);
    }

    public ProjectItem? FindItem(string itemId)
    {
        EnsureLoaded();
        foreach (var project in _backingProjects.Values)
        {
            var found = project.Items.FirstOrDefault(i => i.Id == itemId);
            if (found is not null) return found;
        }
        return null;
    }

    public void ActivateBackingItem(string botId, string itemId)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        if (bot is null) return;
        var backing = EnsureBackingProject(bot);
        if (backing.Items.All(i => i.Id != itemId)) return;
        backing.ActiveItemId = itemId;

        var chat = bot.Chats.FirstOrDefault(c => c.Id == itemId);
        if (chat is not null && bot.ActiveChatId != chat.Id)
        {
            _store.Update(d =>
            {
                if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
                {
                    target.ActiveChatId = chat.Id;
                }
                if (d.ActiveBotId == botId) d.ActiveChatId = chat.Id;
            });
        }
        Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.ActiveChat, FindBot(botId), chat, backing.Items.FirstOrDefault(i => i.Id == itemId)));
    }

    public void CloseBackingItem(string botId, string itemId)
    {
        EnsureLoaded();
        var bot = FindBot(botId);
        if (bot is null) return;
        if (bot.Chats.Any(c => c.Id == itemId))
        {
            CloseChat(botId, itemId);
            return;
        }
        if (_backingProjects.TryGetValue(botId, out var backing)
            && backing.Items.FirstOrDefault(i => i.Id == itemId) is { } fileItem)
        {
            backing.Items.Remove(fileItem);
            if (backing.ActiveItemId == itemId)
            {
                backing.ActiveItemId = backing.Items.FirstOrDefault()?.Id;
            }
            Changed?.Invoke(this, new BotChangedEventArgs(BotChangeKind.Files, bot, Item: fileItem));
        }
    }

    public void SetDockLayout(string botId, string? layout)
    {
        EnsureLoaded();
        if (_backingProjects.TryGetValue(botId, out var backing))
        {
            backing.DockLayout = layout;
        }
        _store.Update(d =>
        {
            if (d.Bots.FirstOrDefault(b => b.Id == botId) is { } target)
            {
                target.DockLayout = layout;
            }
        });
    }

    public string GetHomePath(BotDefinition bot) => Home.GetHomePath(bot);

    public int TotalNeedsAttentionCount()
    {
        EnsureLoaded();
        var count = 0;
        foreach (var bot in _store.Current.Bots)
        {
            count += bot.Items.Count(i => i.Status == BotWorkItemStatus.NeedsYou);
            foreach (var chat in bot.Chats)
            {
                if (chat.Status == BotChatStatus.NeedsYou
                    || (_activity is not null && _activity.Of(chat.Id) == ItemActivity.Waiting))
                {
                    count++;
                }
            }
        }
        return count;
    }

    public bool HasWorkingBots()
    {
        EnsureLoaded();
        foreach (var bot in _store.Current.Bots)
        {
            if (bot.Items.Any(i => i.Status == BotWorkItemStatus.Working)) return true;
            foreach (var chat in bot.Chats)
            {
                if (chat.Status == BotChatStatus.Working
                    || (_activity is not null && _activity.Of(chat.Id) == ItemActivity.Working))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private BotDefinition? FindBot(string botId) => _store.Current.Bots.FirstOrDefault(b => b.Id == botId);

    private Project EnsureBackingProject(BotDefinition bot)
    {
        var homePath = Home.GetHomePath(bot);
        if (!_backingProjects.TryGetValue(bot.Id, out var project))
        {
            project = new Project
            {
                Id = BackingProjectPrefix + bot.Id,
                Name = bot.Name,
                Path = homePath,
                Color = bot.Color,
                DockLayout = bot.DockLayout,
                ActiveItemId = bot.ActiveChatId,
                CreatedAt = bot.CreatedAt,
            };
            _backingProjects[bot.Id] = project;
        }
        else
        {
            project.Name = bot.Name;
            project.Path = homePath;
            project.Color = bot.Color;
        }

        foreach (var chat in bot.Chats.Where(c => c.IsOpen))
        {
            if (project.Items.All(i => i.Id != chat.Id))
            {
                project.Items.Add(ToProjectItem(bot, chat));
            }
        }
        if (project.ActiveItemId is null || project.Items.All(i => i.Id != project.ActiveItemId))
        {
            project.ActiveItemId = bot.ActiveChatId is { } active && project.Items.Any(i => i.Id == active)
                ? active
                : project.Items.FirstOrDefault()?.Id;
        }
        return project;
    }

    private ProjectItem ToProjectItem(BotDefinition bot, BotChat chat)
    {
        var agentId = ResolveAgentId(chat.AgentId ?? bot.AgentId);
        var homePath = Home.GetHomePath(bot);
        return new ProjectItem
        {
            Id = chat.Id,
            Kind = ProjectItemKind.Agent,
            AgentId = agentId,
            Title = chat.Title,
            CustomTitle = chat.CustomTitle,
            WorkingDirectory = homePath,
            Surface = chat.Surface ?? ResolveSurface(agentId),
            AcpSessionId = chat.AcpSessionId,
            Launch = string.IsNullOrWhiteSpace(chat.SessionId)
                ? null
                : new AgentLaunchOptions { SessionId = chat.SessionId },
            CreatedAt = chat.CreatedAt,
        };
    }

    private string ResolveAgentId(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && _catalog.Find(requested) is not null)
        {
            return requested;
        }
        var def = _settings.Current.Agents.DefaultAgentId;
        if (!string.IsNullOrWhiteSpace(def) && _catalog.Find(def) is not null)
        {
            return def;
        }
        var installed = _scanner.Current.Installations
            .Select(i => i.AgentId)
            .FirstOrDefault(id => _catalog.Find(id) is not null);
        return installed ?? _catalog.Definitions.FirstOrDefault()?.Id ?? "claude";
    }

    private AgentSurface ResolveSurface(string agentId)
    {
        var definition = _catalog.Find(agentId);
        if (definition is null) return AgentSurface.Terminal;
        return AgentSurfaceResolver.Resolve(definition, _settings.Current.Agents, null, acpAvailable: true);
    }
}
