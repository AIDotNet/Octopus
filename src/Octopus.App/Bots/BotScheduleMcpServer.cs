using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Octopus.App.Collab;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Bots;

namespace Octopus.App.Bots;

/// <summary>
/// The <c>octopus-bot</c> MCP server: lets a bot schedule its own future work through <see cref="BotScheduler"/>, the
/// same way for every agent. Only bot chats get it: tokens are issued for bot chats alone, and every call checks again
/// that the caller is a chat of a bot.
/// </summary>
public sealed class BotScheduleMcpServer : ICollabToolHandler, IDisposable
{
    public const string ServerName = "octopus-bot";

    private const string Instructions =
        "Octopus bot tools. Use schedule_create to run a prompt in this bot at a later time or on a recurring cron schedule " +
        "(reminders, daily reports, periodic checks). Octopus saves schedules with the bot and delivers each run into this chat, " +
        "so they survive closing the chat and restarting. Prefer these over any built-in, session-only cron tool. " +
        "Use schedule_list to see schedules and schedule_delete to cancel one.";

    private readonly CollabMcpServer _server;
    private readonly IBotService _bots;
    private readonly BotScheduler _scheduler;
    private readonly TimeProvider _time;

    public BotScheduleMcpServer(IBotService bots, BotScheduler scheduler, TimeProvider? time = null)
    {
        _bots = bots;
        _scheduler = scheduler;
        _time = time ?? TimeProvider.System;
        _server = new CollabMcpServer(ServerName, Instructions, BuildToolSchemas);
        _server.AttachHandler(this);
    }

    public bool Start() => _server.Start();

    /// <summary>The MCP server entry for one bot chat's ACP session, or null when the chat is not a bot's.</summary>
    public AcpMcpServerConfig? BuildAcpServerConfig(string chatId)
    {
        if (_bots.FindBotByChatId(chatId) is null) return null;
        return _server.BuildAcpServerConfig(chatId, ItemLaunchService.ResolveForwarderPath());
    }

    public async Task<JsonObject> CallToolAsync(string callerItemId, string toolName, JsonObject? arguments, CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await CallOnUiAsync(callerItemId, toolName, arguments).ConfigureAwait(true);
        }
        return await Dispatcher.UIThread.InvokeAsync(() => CallOnUiAsync(callerItemId, toolName, arguments)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task<JsonObject> CallOnUiAsync(string callerItemId, string toolName, JsonObject? args)
    {
        var bot = _bots.FindBotByChatId(callerItemId);
        if (bot is null)
        {
            return Task.FromResult(Error("These tools are only available inside an Octopus bot chat."));
        }

        try
        {
            return Task.FromResult(toolName switch
            {
                "schedule_create" => Create(bot, callerItemId, args),
                "schedule_list" => List(bot.Id),
                "schedule_delete" => Delete(bot.Id, Str(args, "id")),
                _ => Error($"Unknown tool: {toolName}"),
            });
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(Error(ex.Message));
        }
    }

    private JsonObject Create(BotDefinition bot, string chatId, JsonObject? args)
    {
        var prompt = Str(args, "prompt") ?? "";
        var cron = Str(args, "cron");
        var runAtText = Str(args, "run_at");
        DateTimeOffset? runAt = null;
        if (!string.IsNullOrWhiteSpace(runAtText))
        {
            runAt = ParseLocalTime(runAtText)
                ?? throw new ArgumentException($"run_at '{runAtText}' is not an ISO 8601 date-time such as 2026-11-27T09:00.");
        }

        var schedule = _scheduler.Create(bot.Id, prompt, cron, runAt, Str(args, "title"), chatId, createdBy: "agent");
        var next = schedule.NextRunAt is { } n ? FormatLocal(n) : "unknown";
        var kind = schedule.IsRecurring ? $"recurring ({schedule.Cron})" : "one-shot";
        return Text($"Scheduled {kind} run {schedule.Id}. Next run: {next} (local time). Octopus will send the prompt into this chat; it is saved with the bot and shown in the bot panel. Cancel with schedule_delete id={schedule.Id}.");
    }

    private JsonObject List(string botId)
    {
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == botId);
        var schedules = bot?.Schedules ?? [];
        if (schedules.Count == 0) return Text("No schedules.");

        var sb = new StringBuilder();
        foreach (var s in schedules.OrderBy(s => s.IsBuiltIn).ThenBy(s => s.NextRunAt ?? DateTimeOffset.MaxValue))
        {
            sb.Append("- ").Append(s.Id);
            if (s.IsBuiltIn) sb.Append(" [built-in, owner controls it]");
            sb.Append(s.IsRecurring ? $" cron \"{s.Cron}\"" : $" once at {(s.RunAt is { } at ? FormatLocal(at) : "?")}");
            sb.Append(s.Enabled ? (s.NextRunAt is { } n ? $", next {FormatLocal(n)}" : "") : ", off");
            if (s.LastRunStatus is { } status) sb.Append($", last run {status}");
            if (!string.IsNullOrWhiteSpace(s.Title)) sb.Append($", title \"{s.Title}\"");
            sb.Append(": ").AppendLine(Shorten(s.Prompt, 160));
        }
        return Text(sb.ToString().TrimEnd());
    }

    private JsonObject Delete(string botId, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Error("id is required.");
        var schedule = _bots.Bots.FirstOrDefault(b => b.Id == botId)?.Schedules.FirstOrDefault(s => s.Id == id);
        if (schedule is null) return Error($"No schedule with id {id}.");
        if (schedule.IsBuiltIn) return Error("Built-in automations (look-around, memory maintenance) are switched on and off by the owner in the bot panel.");
        _scheduler.Delete(botId, id);
        return Text($"Deleted schedule {id}.");
    }

    private DateTimeOffset? ParseLocalTime(string text)
    {
        var styles = DateTimeStyles.AllowWhiteSpaces;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var withOffset)
            && (text.EndsWith('Z') || text.LastIndexOfAny(['+', '-']) > 9))
        {
            return withOffset;
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, styles, out var local))
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, _time.LocalTimeZone.GetUtcOffset(unspecified));
        }
        return null;
    }

    private string FormatLocal(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("yyyy-MM-dd HH:mm (ddd)", CultureInfo.InvariantCulture);

    private static string Shorten(string text, int max)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length > max ? single[..max] + "…" : single;
    }

    private static string? Str(JsonObject? args, string name)
    {
        if (args?[name] is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
    }

    private static JsonObject Text(string text) => new()
    {
        ["content"] = new JsonArray { (JsonNode)new JsonObject { ["type"] = "text", ["text"] = text } },
    };

    private static JsonObject Error(string text)
    {
        var result = Text(text);
        result["isError"] = true;
        return result;
    }

    public static JsonArray BuildToolSchemas() => new(
        Tool(
            "schedule_create",
            "Schedule a prompt to run in this bot later. Give `cron` for a recurring run (standard 5-field cron in the owner's local time, e.g. \"3 9 * * *\" every day at 09:03, \"0 9 * * 1-5\" weekdays at 09:00) or `run_at` for a single run (ISO 8601 local date-time, e.g. \"2026-11-27T15:30\"). At each run Octopus sends the prompt into this chat as a new message, so write it as an instruction to yourself (e.g. \"Remind the owner to tidy up their documents.\"). Saved with the bot; runs while Octopus is open.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "What to do at each run." },
                    ["cron"] = new JsonObject { ["type"] = "string", ["description"] = "Recurring: 5-field cron in local time." },
                    ["run_at"] = new JsonObject { ["type"] = "string", ["description"] = "One-shot: ISO 8601 local date-time." },
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Short label for the owner's panel (optional)." },
                },
                ["required"] = new JsonArray("prompt"),
            }),
        Tool(
            "schedule_list",
            "List this bot's schedules with their ids, timing, next run and last result.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        Tool(
            "schedule_delete",
            "Cancel one of this bot's schedules by id.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Schedule id from schedule_create or schedule_list." },
                },
                ["required"] = new JsonArray("id"),
            }));

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    public void Dispose() => _server.Dispose();
}
