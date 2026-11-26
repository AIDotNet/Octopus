using System.Text.Json;
using Octopus.Core.Bots;
using Octopus.Platform;
using Xunit;

namespace Octopus.Storage.Tests;

public sealed class BotGroupStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("octopus-bot-group-store-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private FixedAppPaths Paths() => new(_dir);

    [Fact]
    public void PersistsIndexAndRoomAtomically_AndReloadsCleanly()
    {
        var store = new JsonBotGroupStore(Paths());
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        store.Update(d =>
        {
            d.Groups.Add(new BotGroupDefinition
            {
                Id = "grp-alpha",
                Name = "Design Council",
                HostDeviceKey = "local",
                Revision = 3,
                MaxHops = 5,
                ReplyMode = new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Bot, BotId = "b1" },
                Members =
                [
                    new BotGroupMember { BotId = "b1", ChatId = "chat-b1", StoppedThroughSequence = 2 },
                    new BotGroupMember { BotId = "b2", ChatId = "chat-b2", StoppedThroughSequence = 0 },
                ],
                FormerChatIds = ["chat-old-1"],
                Threads =
                [
                    new BotGroupThread
                    {
                        Id = "thr-1",
                        Title = "API review",
                        Members =
                        [
                            new BotGroupMember { BotId = "b1", ChatId = "thr-chat-b1" },
                            new BotGroupMember { BotId = "b2", ChatId = "thr-chat-b2" },
                        ],
                        FormerChatIds = ["thr-old-1"],
                        CreatedAt = now,
                    },
                ],
                IsExpanded = false,
                IsUnread = true,
                Latest = new BotGroupLatestPreview
                {
                    AuthorName = "Sage",
                    Text = "Looks good to ship.",
                    At = now,
                },
                CreatedAt = now,
            });
        });

        store.UpdateRoom("grp-alpha", room =>
        {
            room.NextSequence = 3;
            room.Messages.Add(new BotGroupMessage
            {
                Id = "msg-1",
                GroupId = "grp-alpha",
                Sequence = 1,
                IsOwner = true,
                Text = "@Sage check PR #42",
                HopDepth = 0,
                CreatedAt = now,
                Turns =
                [
                    new BotGroupTurn
                    {
                        OperationId = "op-1",
                        BotId = "b1",
                        BotName = "Sage",
                        BotColor = "#2f9e63",
                        BotIcon = "Bot",
                        AgentId = "claude",
                        ChatId = "chat-b1",
                        Status = BotGroupTurnStatus.Completed,
                        Reply = "Looks good to ship.",
                        HandedOff = ["b2"],
                        CreatedAt = now,
                        CompletedAt = now.AddSeconds(5),
                    },
                ],
            });
            room.PendingDeliveries.Add(new BotGroupPendingDelivery
            {
                OperationId = "op-2",
                MessageId = "msg-2",
                GroupId = "grp-alpha",
                ThreadId = "thr-1",
                ContextJson = "[{\"authorKind\":\"owner\",\"authorName\":\"Owner\",\"text\":\"hi\"}]",
                CreatedAt = now,
            });
        });

        Assert.Null(store.LastError);
        var indexPath = Path.Combine(_dir, "bot-groups.json");
        var roomPath = Path.Combine(_dir, "bot-groups", "grp-alpha.json");
        Assert.True(File.Exists(indexPath));
        Assert.True(File.Exists(roomPath));

        var reloaded = new JsonBotGroupStore(Paths());
        reloaded.Load();

        var group = Assert.Single(reloaded.Current.Groups);
        Assert.Equal("grp-alpha", group.Id);
        Assert.Equal("Design Council", group.Name);
        Assert.Equal(3, group.Revision);
        Assert.Equal(5, group.MaxHops);
        Assert.Equal(BotGroupReplyModeKind.Bot, group.ReplyMode.Kind);
        Assert.Equal("b1", group.ReplyMode.BotId);
        Assert.Equal(2, group.Members.Count);
        Assert.Equal(2, group.Members[0].StoppedThroughSequence);
        Assert.Equal(["chat-old-1"], group.FormerChatIds);
        Assert.False(group.IsExpanded);
        Assert.True(group.IsUnread);
        Assert.NotNull(group.Latest);
        Assert.Equal("Sage", group.Latest!.AuthorName);
        Assert.Equal("Looks good to ship.", group.Latest.Text);

        var thread = Assert.Single(group.Threads);
        Assert.Equal("thr-1", thread.Id);
        Assert.Equal("API review", thread.Title);
        Assert.Equal(2, thread.Members.Count);
        Assert.Equal(["thr-old-1"], thread.FormerChatIds);

        var reloadedRoom = reloaded.ReadRoom("grp-alpha");
        Assert.Equal("grp-alpha", reloadedRoom.GroupId);
        Assert.Equal(3, reloadedRoom.NextSequence);
        var msg = Assert.Single(reloadedRoom.Messages);
        Assert.Equal("msg-1", msg.Id);
        var turn = Assert.Single(msg.Turns);
        Assert.Equal(BotGroupTurnStatus.Completed, turn.Status);
        Assert.Equal("Looks good to ship.", turn.Reply);
        Assert.Equal(["b2"], turn.HandedOff);

        var pending = Assert.Single(reloadedRoom.PendingDeliveries);
        Assert.Equal("op-2", pending.OperationId);
        Assert.Equal("thr-1", pending.ThreadId);
    }

    [Fact]
    public void Normalize_DeduplicatesClampsResetsReplyModeTruncatesTitlesAndSortsMessages()
    {
        var longTitle = new string('A', 120);
        var doc = JsonBotGroupStore.Normalize(new BotGroupsDocument
        {
            Version = 0,
            Groups =
            [
                new BotGroupDefinition
                {
                    Id = "  g1  ",
                    Name = "  ",
                    HostDeviceKey = "",
                    Revision = 0,
                    MaxHops = 999,
                    ReplyMode = new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Bot, BotId = "non-member" },
                    Members =
                    [
                        new BotGroupMember { BotId = " b1 ", ChatId = " c1 ", StoppedThroughSequence = -5 },
                        new BotGroupMember { BotId = "b1", ChatId = "c1-dup" },
                        new BotGroupMember { BotId = "", ChatId = "c-blank" },
                        new BotGroupMember { BotId = "b2", ChatId = "" },
                    ],
                    FormerChatIds = ["  old-1 ", "old-1", "", "old-2"],
                    Threads =
                    [
                        new BotGroupThread
                        {
                            Id = " t1 ",
                            Title = longTitle,
                            Members =
                            [
                                new BotGroupMember { BotId = "b1", ChatId = "tc1" },
                                new BotGroupMember { BotId = "b1", ChatId = "tc1-dup" },
                            ],
                            FormerChatIds = ["tf1", " tf1 "],
                        },
                        new BotGroupThread { Id = "t1", Title = "duplicate-thread" },
                        new BotGroupThread { Id = "   ", Title = "blank-id" },
                    ],
                },
                new BotGroupDefinition
                {
                    Id = "g1",
                    Name = "Duplicate Group",
                },
                new BotGroupDefinition
                {
                    Id = "g2",
                    Name = "Zero Hops Group",
                    MaxHops = 0,
                    Members = [new BotGroupMember { BotId = "b9", ChatId = "c9" }],
                    ReplyMode = new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Bot, BotId = "b9" },
                },
            ],
        });

        Assert.Equal(1, doc.Version);
        Assert.Equal(2, doc.Groups.Count);

        var g1 = doc.Groups[0];
        Assert.Equal("g1", g1.Id);
        Assert.Equal("Bot Group", g1.Name);
        Assert.Equal("local", g1.HostDeviceKey);
        Assert.Equal(1, g1.Revision);
        Assert.Equal(30, g1.MaxHops);
        Assert.Equal(BotGroupReplyModeKind.Mentions, g1.ReplyMode.Kind);
        Assert.Null(g1.ReplyMode.BotId);

        var member = Assert.Single(g1.Members);
        Assert.Equal("b1", member.BotId);
        Assert.Equal("c1", member.ChatId);
        Assert.Equal(0, member.StoppedThroughSequence);
        Assert.Equal(["old-1", "old-2"], g1.FormerChatIds);

        var thread = Assert.Single(g1.Threads);
        Assert.Equal("t1", thread.Id);
        Assert.NotNull(thread.Title);
        Assert.Equal(80, thread.Title!.Length);
        Assert.Single(thread.Members);
        Assert.Equal(["tf1"], thread.FormerChatIds);

        var g2 = doc.Groups[1];
        Assert.Equal(BotGroupDefinition.DefaultMaxHops, g2.MaxHops);
        Assert.Equal(BotGroupReplyModeKind.Bot, g2.ReplyMode.Kind);
        Assert.Equal("b9", g2.ReplyMode.BotId);

        var room = JsonBotGroupStore.NormalizeRoom(new BotGroupRoomDocument
        {
            Version = 0,
            NextSequence = 1,
            Messages =
            [
                new BotGroupMessage
                {
                    Id = "m3",
                    Sequence = 10,
                    Turns =
                    [
                        new BotGroupTurn { OperationId = "op-a" },
                        new BotGroupTurn { OperationId = "op-a" },
                        new BotGroupTurn { OperationId = "" },
                    ],
                },
                new BotGroupMessage { Id = "m1", Sequence = 2 },
                new BotGroupMessage { Id = "m1", Sequence = 99 },
                new BotGroupMessage { Id = "  ", Sequence = 5 },
                new BotGroupMessage { Id = "m2", Sequence = 5 },
            ],
            PendingDeliveries =
            [
                new BotGroupPendingDelivery { OperationId = "p1", MessageId = "m1" },
                new BotGroupPendingDelivery { OperationId = "p1", MessageId = "m1-dup" },
                new BotGroupPendingDelivery { OperationId = "", MessageId = "m2" },
            ],
        }, " g1 ");

        Assert.Equal(1, room.Version);
        Assert.Equal("g1", room.GroupId);
        Assert.Equal(["m1", "m2", "m3"], room.Messages.Select(m => m.Id).ToArray());
        Assert.Equal([2L, 5L, 10L], room.Messages.Select(m => m.Sequence).ToArray());
        Assert.All(room.Messages, m => Assert.Equal("g1", m.GroupId));
        Assert.Single(room.Messages[2].Turns);
        Assert.Equal(11, room.NextSequence);
        Assert.Single(room.PendingDeliveries);
    }

    [Fact]
    public void CorruptIndexOrRoomFile_IsQuarantinedGracefullyWithoutCrashing()
    {
        var indexPath = Path.Combine(_dir, "bot-groups.json");
        var roomsDir = Path.Combine(_dir, "bot-groups");
        Directory.CreateDirectory(roomsDir);
        var roomPath = Path.Combine(roomsDir, "grp-broken.json");

        File.WriteAllText(indexPath, "{ invalid json !!!");
        File.WriteAllText(roomPath, "[not a valid room document");

        var store = new JsonBotGroupStore(Paths());
        store.Load();

        Assert.Empty(store.Current.Groups);
        Assert.False(File.Exists(indexPath));
        Assert.NotEmpty(Directory.GetFiles(_dir, "bot-groups.json.corrupt-*"));

        var room = store.ReadRoom("grp-broken");
        Assert.Equal("grp-broken", room.GroupId);
        Assert.Empty(room.Messages);
        Assert.Equal(1, room.NextSequence);
        Assert.False(File.Exists(roomPath));
        Assert.NotEmpty(Directory.GetFiles(roomsDir, "grp-broken.json.corrupt-*"));
    }

    [Fact]
    public void DeleteRoom_RemovesRoomFileFromDiskAndClearsCache()
    {
        var store = new JsonBotGroupStore(Paths());
        store.UpdateRoom("grp-del", room =>
        {
            room.Messages.Add(new BotGroupMessage { Id = "m1", Sequence = 1, Text = "hello" });
        });

        var roomPath = Path.Combine(_dir, "bot-groups", "grp-del.json");
        Assert.True(File.Exists(roomPath));

        store.DeleteRoom("grp-del");

        Assert.False(File.Exists(roomPath));
        var fresh = store.ReadRoom("grp-del");
        Assert.Empty(fresh.Messages);
        Assert.Equal(1, fresh.NextSequence);
    }

    [Fact]
    public void OctopusJsonContext_FileOptions_RoundTripsCamelCaseEnumsAndNestedStructures()
    {
        var now = new DateTimeOffset(2026, 11, 26, 15, 30, 0, TimeSpan.Zero);
        var indexDoc = new BotGroupsDocument
        {
            Version = 1,
            Groups =
            [
                new BotGroupDefinition
                {
                    Id = "g-aot",
                    Name = "AOT Squad",
                    ReplyMode = new BotGroupReplyMode { Kind = BotGroupReplyModeKind.All },
                    Members = [new BotGroupMember { BotId = "b1", ChatId = "c1", StoppedThroughSequence = 4 }],
                    Threads =
                    [
                        new BotGroupThread
                        {
                            Id = "t-aot",
                            Title = "Perf audit",
                            Members = [new BotGroupMember { BotId = "b1", ChatId = "tc1" }],
                            CreatedAt = now,
                        },
                    ],
                    Latest = new BotGroupLatestPreview { AuthorName = "Bot A", Text = "Done", At = now },
                    CreatedAt = now,
                },
                new BotGroupDefinition
                {
                    Id = "g-aot-2",
                    Name = "Mentions Squad",
                    ReplyMode = new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Mentions },
                },
            ],
        };

        var indexJson = JsonSerializer.Serialize(indexDoc, OctopusJsonContext.FileTypeInfo<BotGroupsDocument>());
        Assert.Contains("\"kind\": \"all\"", indexJson);
        Assert.Contains("\"kind\": \"mentions\"", indexJson);

        var decodedIndex = JsonSerializer.Deserialize(indexJson, OctopusJsonContext.FileTypeInfo<BotGroupsDocument>());
        Assert.NotNull(decodedIndex);
        Assert.Equal(2, decodedIndex!.Groups.Count);
        Assert.Equal(BotGroupReplyModeKind.All, decodedIndex.Groups[0].ReplyMode.Kind);
        Assert.Equal(BotGroupReplyModeKind.Mentions, decodedIndex.Groups[1].ReplyMode.Kind);
        Assert.Equal("Perf audit", Assert.Single(decodedIndex.Groups[0].Threads).Title);

        var roomDoc = new BotGroupRoomDocument
        {
            Version = 1,
            GroupId = "g-aot",
            NextSequence = 4,
            Messages =
            [
                new BotGroupMessage
                {
                    Id = "m1",
                    GroupId = "g-aot",
                    Sequence = 1,
                    IsOwner = false,
                    IsMentionHandoff = true,
                    PausedByHopLimit = true,
                    PausedTargetBotIds = ["b2"],
                    PausedSourceOperationId = "op-src",
                    RootRequestId = "m0",
                    RootSequence = 1,
                    HopDepth = 3,
                    RequesterBotId = "b1",
                    RequesterBotName = "Bot A",
                    Text = "Paused",
                    Turns =
                    [
                        new BotGroupTurn { OperationId = "o1", BotId = "b1", Status = BotGroupTurnStatus.Queued },
                        new BotGroupTurn { OperationId = "o2", BotId = "b2", Status = BotGroupTurnStatus.Running },
                        new BotGroupTurn { OperationId = "o3", BotId = "b3", Status = BotGroupTurnStatus.Completed, Reply = "ok" },
                        new BotGroupTurn { OperationId = "o4", BotId = "b4", Status = BotGroupTurnStatus.Stopped },
                        new BotGroupTurn { OperationId = "o5", BotId = "b5", Status = BotGroupTurnStatus.Failed, Error = "timeout" },
                    ],
                },
            ],
            PendingDeliveries =
            [
                new BotGroupPendingDelivery { OperationId = "o1", MessageId = "m1", GroupId = "g-aot", ContextJson = "[]" },
            ],
        };

        var roomJson = JsonSerializer.Serialize(roomDoc, OctopusJsonContext.FileTypeInfo<BotGroupRoomDocument>());
        Assert.Contains("\"status\": \"queued\"", roomJson);
        Assert.Contains("\"status\": \"running\"", roomJson);
        Assert.Contains("\"status\": \"completed\"", roomJson);
        Assert.Contains("\"status\": \"stopped\"", roomJson);
        Assert.Contains("\"status\": \"failed\"", roomJson);

        var decodedRoom = JsonSerializer.Deserialize(roomJson, OctopusJsonContext.FileTypeInfo<BotGroupRoomDocument>());
        Assert.NotNull(decodedRoom);
        var decodedMsg = Assert.Single(decodedRoom!.Messages);
        Assert.True(decodedMsg.PausedByHopLimit);
        Assert.Equal(["b2"], decodedMsg.PausedTargetBotIds);
        Assert.Equal("op-src", decodedMsg.PausedSourceOperationId);
        Assert.Equal(
            [
                BotGroupTurnStatus.Queued,
                BotGroupTurnStatus.Running,
                BotGroupTurnStatus.Completed,
                BotGroupTurnStatus.Stopped,
                BotGroupTurnStatus.Failed,
            ],
            decodedMsg.Turns.Select(t => t.Status).ToArray());
        Assert.Single(decodedRoom.PendingDeliveries);
    }
}
