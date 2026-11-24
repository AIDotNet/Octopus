using Avalonia.Threading;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;

namespace Octopus.Mobile.Demo;

// A made-up GUI conversation for the demo's GUI session ("接口文档整理" on "工作站"). It plays the host: it keeps the
// entries in the wire format and sends them through the same ConversationTimeline a real host's updates go through,
// so the pages and the tests see what they would see from a computer.
public sealed partial class DemoWorkspace
{
    /// <summary>The made-up computers speak <c>acp.gui</c>; false shows how a GUI session opens on an older one (its details, with a note).</summary>
    public bool ConversationsSupported { get; set; } = true;

    /// <summary>How long the made-up agent takes for each step of a turn.</summary>
    public TimeSpan ConversationStep { get; set; } = TimeSpan.FromMilliseconds(900);

    /// <summary>The conversations opened, for tests: the last one of each session.</summary>
    internal Dictionary<string, DemoConversation> Conversations { get; } = new(StringComparer.Ordinal);

    public bool SupportsConversations(RemoteHost host) => ConversationsSupported;

    public Task<RemoteConversation> OpenConversationAsync(RemoteSession session, CancellationToken cancellationToken = default)
    {
        if (!ConversationsSupported || !session.IsGui) throw new IOException("GUI conversations are not supported.");
        var conversation = new DemoConversation(session, this);
        Conversations[session.Id] = conversation;
        return Task.FromResult<RemoteConversation>(conversation);
    }

    /// <summary>The made-up conversation: what a host would keep, and the updates it would send.</summary>
    internal sealed class DemoConversation : RemoteConversation
    {
        private readonly DemoWorkspace _workspace;
        private readonly List<AcpEntryMessage> _entries;
        private readonly AcpComposerMessage _composer;
        private readonly Queue<string> _queued = new();
        private long _sequence;
        private int _nextId = 1;
        private string _state;
        private bool _disposed;

        public DemoConversation(RemoteSession session, DemoWorkspace workspace) : base(session)
        {
            _workspace = workspace;
            _composer = new AcpComposerMessage
            {
                Models = [new() { Id = "gpt-6", Label = "GPT-6" }, new() { Id = "gpt-6-mini", Label = "GPT-6 mini" }],
                Model = "gpt-6",
                Modes = [new() { Id = "ask", Label = "询问权限" }, new() { Id = "auto-edit", Label = "自动编辑" }, new() { Id = "full", Label = "完全放开" }],
                Mode = "ask",
                Efforts = [new() { Id = "low", Label = "低" }, new() { Id = "medium", Label = "中" }, new() { Id = "high", Label = "高" }],
                Effort = "high",
            };
            _entries = session.Title == "接口文档整理" ? History() : [];
            _state = _entries.Any(entry => entry.Permission is { IsResolved: false }) ? AcpStates.Waiting : AcpStates.Ready;
            Timeline.ApplySnapshot(new AcpSnapshotMessage
            {
                ItemId = session.Id,
                Sequence = _sequence,
                State = _state,
                AgentName = "Codex",
                Entries = [.. _entries],
                HasEarlier = _entries.Count > 0,
                Composer = _composer,
            });
            ShowActivity();
        }

        /// <summary>The prompts sent, in order, for tests.</summary>
        public List<string> Sent { get; } = [];

        public override Task SendAsync(string text, IReadOnlyList<string>? attachments = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(text);
            // A turn runs: the host queues it, as the desktop's tab does.
            if (_state is AcpStates.Working or AcpStates.Waiting)
            {
                _queued.Enqueue(text);
                Send(new AcpUpdateMessage { QueuedCount = _queued.Count });
                return Task.CompletedTask;
            }
            StartTurn(text);
            return Task.CompletedTask;
        }

        public override Task CancelAsync(CancellationToken cancellationToken = default)
        {
            if (_state is not (AcpStates.Working or AcpStates.Waiting)) return Task.CompletedTask;
            var update = new AcpUpdateMessage { State = AcpStates.Ready, QueuedCount = 0 };
            _queued.Clear();
            foreach (var entry in _entries)
            {
                if (entry.Permission is { IsResolved: false } permission)
                {
                    permission.IsResolved = true;
                    permission.ResolvedSummary = "已取消";
                    update.Upserts.Add(entry);
                }
                else if (entry.IsStreaming)
                {
                    entry.IsStreaming = false;
                    entry.ErrorText = "已停止";
                    update.Upserts.Add(entry);
                }
            }
            Send(update);
            return Task.CompletedTask;
        }

        public override Task RespondAsync(ConversationPermissionEntry card, string optionId, CancellationToken cancellationToken = default)
        {
            var entry = _entries.FirstOrDefault(known => known.Id == card.Id);
            if (entry?.Permission is not { IsResolved: false } permission) throw new IOException("这个请求已经处理过了。");
            var option = permission.Options.FirstOrDefault(known => known.Id == optionId) ?? throw new IOException("没有这个选项。");
            permission.IsResolved = true;
            permission.ResolvedSummary = option.Label;
            var turn = _entries.LastOrDefault(known => known.Kind == AcpEntryKinds.Assistant);
            var allowed = option.Detail is "allow_once" or "allow_always";
            var update = new AcpUpdateMessage { Upserts = [entry], State = allowed ? AcpStates.Working : AcpStates.Ready };
            if (turn is not null && allowed)
            {
                // The command runs, and the plan moves on.
                turn.IsStreaming = true;
                turn.Tools = [.. turn.Tools ?? [], new AcpToolMessage
                {
                    Id = "t" + _nextId++, Title = "运行 pnpm install", Kind = "execute", Status = "in_progress", Command = permission.Detail,
                }];
                turn.Plan = Plan("completed", "completed", "in_progress");
                update.Upserts.Add(turn);
            }
            else if (turn is not null)
            {
                turn.Text += "\n\n好的，不安装依赖。站点等你装好依赖后再构建。";
                update.Upserts.Add(turn);
            }
            Send(update);
            if (turn is not null && allowed)
            {
                Later(() =>
                {
                    var tool = turn.Tools![^1];
                    tool.Status = "completed";
                    tool.ExitCode = 0;
                    tool.Output = "Lockfile is up to date\nPackages: +214\nDone in 3.2s";
                    turn.Plan = Plan("completed", "completed", "completed");
                    turn.Text += "\n\n依赖装好了，`pnpm docs:build` 也通过了，站点在 `docs/.vitepress/dist`。";
                    turn.IsStreaming = false;
                    turn.Duration = "1分12秒";
                    Send(new AcpUpdateMessage { Upserts = [turn], State = AcpStates.Ready });
                    NextQueued();
                });
            }
            return Task.CompletedTask;
        }

        public override Task AnswerAsync(ConversationQuestionEntry card, IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
            IReadOnlyDictionary<string, string> freeText, CancellationToken cancellationToken = default)
        {
            var entry = _entries.FirstOrDefault(known => known.Id == card.Id);
            if (entry?.Question is not { IsResolved: false } question) throw new IOException("这个问题已经回答过了。");
            var parts = new List<string>();
            foreach (var field in question.Fields)
            {
                parts.AddRange(field.Choices.Where(choice => answers.GetValueOrDefault(field.Id)?.Contains(choice.Id) == true).Select(choice => choice.Label));
                if (freeText.GetValueOrDefault(field.Id) is { Length: > 0 } typed) parts.Add(typed);
            }
            question.IsResolved = true;
            question.ResolvedSummary = string.Join("、", parts);
            Send(new AcpUpdateMessage { Upserts = [entry], State = AcpStates.Working });
            var reply = NewEntry(AcpEntryKinds.Assistant);
            reply.Text = "好，就按" + question.ResolvedSummary + "来写。";
            Later(() =>
            {
                _entries.Add(reply);
                Send(new AcpUpdateMessage { Upserts = [reply], State = AcpStates.Ready });
            });
            return Task.CompletedTask;
        }

        public override Task SetOptionAsync(ConversationOptionKind kind, string value, CancellationToken cancellationToken = default)
        {
            switch (kind)
            {
                case ConversationOptionKind.Model: _composer.Model = value; break;
                case ConversationOptionKind.Mode: _composer.Mode = value; break;
                default: _composer.Effort = value; break;
            }
            Send(new AcpUpdateMessage { Composer = _composer });
            return Task.CompletedTask;
        }

        /// <summary>The agent asks a question (for the tests and the screenshots): a card with choices and free text.</summary>
        public void Ask()
        {
            var entry = NewEntry(AcpEntryKinds.Question);
            entry.Question = new AcpQuestionMessage
            {
                Title = "文档要怎么组织？",
                Fields =
                [
                    new()
                    {
                        Id = "layout", Prompt = "按什么分节", Choices =
                        [
                            new() { Id = "module", Label = "按模块", Detail = "用户、订单、支付各一节" },
                            new() { Id = "method", Label = "按请求方法" },
                        ],
                    },
                    new()
                    {
                        Id = "extras", Prompt = "还要附上", MultiSelect = true, AllowFreeText = true, Choices =
                        [
                            new() { Id = "curl", Label = "curl 示例" },
                            new() { Id = "errors", Label = "错误码表" },
                        ],
                    },
                ],
            };
            _entries.Add(entry);
            Send(new AcpUpdateMessage { Upserts = [entry], State = AcpStates.Waiting });
        }

        public override void Dispose()
        {
            _disposed = true;
            base.Dispose();
        }

        private void StartTurn(string text)
        {
            var user = NewEntry(AcpEntryKinds.User);
            user.Text = text;
            var turn = NewEntry(AcpEntryKinds.Assistant);
            turn.IsStreaming = true;
            turn.Thought = "先看一下现在的文档和路由，再决定怎么改。";
            _entries.Add(user);
            _entries.Add(turn);
            Send(new AcpUpdateMessage { Upserts = [user, turn], State = AcpStates.Working });
            Later(() =>
            {
                turn.Text = "收到：" + text + "\n\n这是演示数据，电脑上不会真的执行。";
                turn.Tools = [new AcpToolMessage { Id = "t" + _nextId++, Title = "读取 docs/api.md", Kind = "read", Status = "completed", Path = "docs/api.md" }];
                turn.IsStreaming = false;
                turn.Duration = "3秒";
                Send(new AcpUpdateMessage { Upserts = [turn], State = AcpStates.Ready });
                NextQueued();
            });
        }

        private void NextQueued()
        {
            if (!_queued.TryDequeue(out var next)) return;
            Send(new AcpUpdateMessage { QueuedCount = _queued.Count });
            StartTurn(next);
        }

        // What a host sends: the next sequence number, applied through the same timeline as a real host's updates.
        private void Send(AcpUpdateMessage update)
        {
            if (_disposed) return;
            update.ItemId = Session.Id;
            update.Sequence = ++_sequence;
            if (update.State is { } state) _state = state;
            Timeline.Apply(Copy(update));
            ShowActivity();
        }

        // The host's row says what the conversation does, as the host's item activity would.
        private void ShowActivity() => Session.Activity = _state switch
        {
            AcpStates.Working => SessionActivity.Working,
            AcpStates.Waiting => SessionActivity.Waiting,
            AcpStates.Error => SessionActivity.Error,
            _ => SessionActivity.Idle,
        };

        private void Later(Action step) => DispatcherTimer.RunOnce(() =>
        {
            if (!_disposed) step();
        }, _workspace.ConversationStep);

        private AcpEntryMessage NewEntry(string kind) => new() { Id = "d" + _nextId++, Kind = kind };

        // The wire would hand over a copy: what this side changes later must not reach into what was applied.
        private static AcpUpdateMessage Copy(AcpUpdateMessage update) => new()
        {
            ItemId = update.ItemId,
            Sequence = update.Sequence,
            Upserts = [.. update.Upserts.Select(Copy)],
            Removed = [.. update.Removed],
            State = update.State,
            StatusDetail = update.StatusDetail,
            ErrorText = update.ErrorText,
            QueuedCount = update.QueuedCount,
            Composer = update.Composer is { } composer ? new AcpComposerMessage
            {
                Models = composer.Models, Model = composer.Model, Modes = composer.Modes, Mode = composer.Mode, Efforts = composer.Efforts, Effort = composer.Effort,
            } : null,
        };

        private static AcpEntryMessage Copy(AcpEntryMessage entry) => new()
        {
            Id = entry.Id,
            Kind = entry.Kind,
            AfterId = entry.AfterId,
            Text = entry.Text,
            Thought = entry.Thought,
            IsStreaming = entry.IsStreaming,
            ErrorText = entry.ErrorText,
            Duration = entry.Duration,
            Attachments = entry.Attachments is null ? null : [.. entry.Attachments],
            Tools = entry.Tools?.Select(tool => new AcpToolMessage
            {
                Id = tool.Id, Title = tool.Title, Kind = tool.Kind, Status = tool.Status, Command = tool.Command, Path = tool.Path,
                Added = tool.Added, Removed = tool.Removed, ExitCode = tool.ExitCode, Output = tool.Output, Diff = tool.Diff is null ? null : [.. tool.Diff],
            }).ToList(),
            Plan = entry.Plan?.Select(item => new AcpPlanEntryMessage { Content = item.Content, Status = item.Status }).ToList(),
            Permission = entry.Permission is { } permission ? new AcpPermissionMessage
            {
                Title = permission.Title, Detail = permission.Detail, Warning = permission.Warning, Options = permission.Options,
                IsResolved = permission.IsResolved, ResolvedSummary = permission.ResolvedSummary,
            } : null,
            Question = entry.Question is { } question ? new AcpQuestionMessage
            {
                Title = question.Title, Fields = question.Fields, IsResolved = question.IsResolved, ResolvedSummary = question.ResolvedSummary,
            } : null,
        };

        private static List<AcpPlanEntryMessage> Plan(string sidebar, string install, string build) =>
        [
            new() { Content = "更新侧边栏", Status = sidebar },
            new() { Content = "安装依赖", Status = install },
            new() { Content = "构建站点", Status = build },
        ];

        // The conversation so far: an earlier request done, the current one stopped at a command that needs permission.
        private List<AcpEntryMessage> History() =>
        [
            new() { Id = "u1", Kind = AcpEntryKinds.User, Text = "把 src/routes 里的接口整理成 docs/api.md，每个接口写上参数和返回值" },
            new()
            {
                Id = "a1", Kind = AcpEntryKinds.Assistant, Duration = "48秒",
                Thought = "先列出 src/routes 下的文件，再逐个读路由定义，按模块归类。返回值从各个 handler 的类型里取。",
                Tools =
                [
                    new() { Id = "t1", Title = "搜索 src/routes", Kind = "search", Status = "completed" },
                    new() { Id = "t2", Title = "读取 src/routes/users.ts", Kind = "read", Status = "completed", Path = "src/routes/users.ts" },
                    new()
                    {
                        Id = "t3", Title = "docs/api.md", Kind = "edit", Status = "completed", Path = "docs/api.md", Added = 4, Removed = 1,
                        Diff = [" # API", "-待补充", "+## 用户", "+- `GET /api/users`：分页列出用户", "+- `POST /api/users`：新建用户", "+## 订单"],
                    },
                ],
                Text = "整理好了 **12 个接口**，按模块分成三节：\n\n## 用户\n- `GET /api/users`：分页列出用户\n- `POST /api/users`：新建用户\n\n" +
                       "## 订单\n- `GET /api/orders/{id}`：读取一个订单\n\n```http\nGET /api/orders/42\nAuthorization: Bearer <token>\n```\n\n" +
                       "顺手把 README 里的接口链接也指到了新文档。",
            },
            new() { Id = "u2", Kind = AcpEntryKinds.User, Text = "再把文档发布到 docs 站点，跑一下构建", Attachments = ["站点截图.png"] },
            new()
            {
                Id = "a2", Kind = AcpEntryKinds.Assistant, IsStreaming = true,
                Thought = "站点用的是 VitePress，侧边栏要加上新页面，然后装依赖、构建。",
                Tools =
                [
                    new()
                    {
                        Id = "t4", Title = "docs/.vitepress/config.ts", Kind = "edit", Status = "completed", Path = "docs/.vitepress/config.ts", Added = 1, Removed = 0,
                        Diff = ["   sidebar: [", "     { text: '指南', link: '/guide' },", "+    { text: '接口', link: '/api' },", "   ],"],
                    },
                ],
                Plan = Plan("completed", "in_progress", "pending"),
                Text = "侧边栏加好了。现在需要安装依赖：",
            },
            new()
            {
                Id = "p1", Kind = AcpEntryKinds.Permission,
                Permission = new AcpPermissionMessage
                {
                    Title = "请求运行命令",
                    Detail = "pnpm install --frozen-lockfile",
                    Options =
                    [
                        new() { Id = "once", Label = "允许一次", Detail = "allow_once" },
                        new() { Id = "always", Label = "总是允许", Detail = "allow_always" },
                        new() { Id = "reject", Label = "拒绝", Detail = "reject_once" },
                    ],
                },
            },
        ];
    }
}
