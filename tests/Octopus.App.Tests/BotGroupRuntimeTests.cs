using System.Text.Json;
using Octopus.Agents;
using Octopus.App.Bots;
using Octopus.App.Bots.Groups;
using Octopus.App.Common;
using Octopus.Core.Bots;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

public sealed class BotGroupRuntimeTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("octopus-bot-group-runtime-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch (IOException)
        {
        }
    }

    // ---- BotGroupContextBuilder ----

    [Fact]
    public void ContextBuilder_IncrementalHistory_SkipsBeforeLastSuccessExcludesSelfAndIncludesAfterFailedTurns()
    {
        var member = new BotGroupMember { BotId = "b-target", ChatId = "chat-target" };
        var messages = new List<BotGroupMessage>
        {
            new()
            {
                Id = "m1",
                Sequence = 1,
                IsOwner = true,
                Text = "Old owner post before target succeeded",
                Turns =
                [
                    new BotGroupTurn { OperationId = "o1", BotId = "b-other", BotName = "Other", ChatId = "chat-other", Status = BotGroupTurnStatus.Completed, Reply = "Old other reply" },
                ],
            },
            new()
            {
                Id = "m2",
                Sequence = 2,
                IsOwner = true,
                Text = "Message where target last succeeded",
                Turns =
                [
                    new BotGroupTurn { OperationId = "o2", BotId = "b-target", BotName = "Target", ChatId = "chat-target", Status = BotGroupTurnStatus.Completed, Reply = "My own reply" },
                    new BotGroupTurn { OperationId = "o3", BotId = "b-other", BotName = "Other", ChatId = "chat-other", Status = BotGroupTurnStatus.Completed, Reply = "Sibling reply on same message" },
                ],
            },
            new()
            {
                Id = "m3",
                Sequence = 3,
                IsOwner = true,
                Text = "Post where target failed",
                Turns =
                [
                    new BotGroupTurn { OperationId = "o4", BotId = "b-target", BotName = "Target", ChatId = "chat-target", Status = BotGroupTurnStatus.Failed, Error = "network timeout" },
                    new BotGroupTurn { OperationId = "o5", BotId = "b-other", BotName = "Other", ChatId = "chat-other", Status = BotGroupTurnStatus.Completed, Reply = "Other reply on failed turn message" },
                ],
            },
            new()
            {
                Id = "m4",
                Sequence = 4,
                IsOwner = true,
                Text = "Newest owner post",
                Turns = [],
            },
        };

        var json = BotGroupContextBuilder.BuildIncrementalContextJson(messages, member);
        var texts = ParseContextTexts(json);

        Assert.DoesNotContain("Old owner post before target succeeded", texts);
        Assert.DoesNotContain("Old other reply", texts);
        Assert.DoesNotContain("Message where target last succeeded", texts);
        Assert.DoesNotContain("My own reply", texts);
        Assert.Equal(
            [
                "Sibling reply on same message",
                "Post where target failed",
                "Other reply on failed turn message",
                "Newest owner post",
            ],
            texts);
    }

    [Fact]
    public void ContextBuilder_FitEntriesJson_Respects24KBudgetAndPreservesKeptTail()
    {
        var entries = new List<BotGroupContextBuilder.ContextEntry>();
        for (var i = 0; i < 10; i++)
        {
            entries.Add(new BotGroupContextBuilder.ContextEntry("owner", null, "Owner", $"entry-{i}-" + new string('x', 4_000)));
        }

        var fitted = BotGroupContextBuilder.FitEntriesJson(entries, keptTailCount: 2);
        Assert.True(fitted.Length <= BotGroupContextBuilder.MaxContextChars);

        var texts = ParseContextTexts(fitted);
        Assert.True(texts.Count >= 2);
        Assert.DoesNotContain(texts, t => t.StartsWith("entry-0-", StringComparison.Ordinal));
        Assert.StartsWith("entry-8-", texts[^2], StringComparison.Ordinal);
        Assert.StartsWith("entry-9-", texts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ContextBuilder_WithNamingReply_PreservesMentionExcerptInHugeReplyWithinBudget()
    {
        var baseEntries = new List<BotGroupContextBuilder.ContextEntry>();
        for (var i = 0; i < 5; i++)
        {
            baseEntries.Add(new BotGroupContextBuilder.ContextEntry("owner", null, "Owner", $"old-{i}-" + new string('a', 4_500)));
        }
        var baseJson = BotGroupContextBuilder.FitEntriesJson(baseEntries);

        var prefix = new string('P', 18_000);
        const string marker = "@Atlas please inspect the race condition here";
        var suffix = new string('S', 18_000);
        var hugeReply = prefix + marker + suffix;
        var speaker = new BotGroupTurn { BotId = "b1", BotName = "Sage" };

        var resultJson = BotGroupContextBuilder.WithNamingReply(baseJson, speaker, hugeReply, prefix.Length);

        Assert.True(resultJson.Length <= BotGroupContextBuilder.MaxContextChars);
        var texts = ParseContextTexts(resultJson);
        Assert.NotEmpty(texts);
        var namingEntry = texts[^1];
        Assert.Contains(marker, namingEntry);
        Assert.StartsWith("…", namingEntry);
        Assert.EndsWith("…", namingEntry);
    }

    // ---- BotGroupService lifecycle ----

    [Fact]
    public void CreateGroup_AllocatesIsolatedGroupMemberChatsForEachMember()
    {
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest("Core Team", [botA.Id, botB.Id]));

        Assert.Equal("Core Team", group.Name);
        Assert.Equal(1, group.Revision);
        Assert.Equal(2, group.Members.Count);
        Assert.Equal(group.Id, harness.Groups.CurrentGroup?.Id);

        var reloadedA = harness.Bots.Bots.Single(b => b.Id == botA.Id);
        var reloadedB = harness.Bots.Bots.Single(b => b.Id == botB.Id);

        var chatA = reloadedA.Chats.Single(c => c.Id == group.Members[0].ChatId);
        var chatB = reloadedB.Chats.Single(c => c.Id == group.Members[1].ChatId);

        Assert.Equal(BotChatKind.GroupMember, chatA.Kind);
        Assert.Equal(group.Id, chatA.GroupId);
        Assert.Null(chatA.GroupThreadId);
        Assert.NotEqual(reloadedA.HomeChatId, chatA.Id);

        Assert.Equal(BotChatKind.GroupMember, chatB.Kind);
        Assert.Equal(group.Id, chatB.GroupId);
        Assert.NotEqual(chatA.Id, chatB.Id);
    }

    [Fact]
    public async Task SendMessageAsync_EnforcesRevisionIdempotencyUpdatesLatestAndDeliversIncrementalPrompts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest(
            "Ops Room",
            [botA.Id, botB.Id],
            new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Mentions }));

        // Stale revision is rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Groups.SendMessageAsync(group.Id, revision: 99, text: "@Sage check status", cancellationToken: ct));

        // First send with explicit messageId.
        var first = await harness.Groups.SendMessageAsync(
            group.Id,
            revision: group.Revision,
            text: "@Sage check deployment",
            messageId: "owner-msg-1",
            cancellationToken: ct);

        Assert.Single(first.Turns);
        var firstDelivery = Assert.Single(harness.Deliveries);
        Assert.Equal(botA.Id, firstDelivery.BotId);
        Assert.Contains("@Sage check deployment", firstDelivery.Prompt);
        Assert.Equal("@Sage check deployment", harness.Groups.FindGroup(group.Id)!.Latest?.Text);
        Assert.Null(harness.Groups.FindGroup(group.Id)!.Latest?.AuthorName);

        // Replaying the same messageId with identical payload is idempotent (no duplicate turns/deliveries).
        var replay = await harness.Groups.SendMessageAsync(
            group.Id,
            revision: group.Revision,
            text: "@Sage check deployment",
            messageId: "owner-msg-1",
            cancellationToken: ct);

        Assert.Equal(first.Id, replay.Id);
        Assert.Single(harness.Deliveries);
        Assert.Single(harness.Groups.ReadRoom(group.Id).Messages);

        // Reusing the same messageId with different text throws.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Groups.SendMessageAsync(
                group.Id,
                revision: group.Revision,
                text: "@Sage different text",
                messageId: "owner-msg-1",
                cancellationToken: ct));

        // Settle Sage's reply; Latest preview updates to Sage's reply.
        await harness.Groups.HandleTurnSettledAsync(
            firstDelivery.ChatId,
            firstDelivery.OperationId,
            "Deployment is green on staging.",
            cancelled: false,
            error: null);

        var latestAfterReply = harness.Groups.FindGroup(group.Id)!.Latest;
        Assert.NotNull(latestAfterReply);
        Assert.Equal("Sage", latestAfterReply!.AuthorName);
        Assert.Equal("Deployment is green on staging.", latestAfterReply.Text);

        // Send a second message to Atlas; incremental context must include owner's first post and Sage's reply.
        await harness.Groups.SendMessageAsync(
            group.Id,
            revision: group.Revision,
            text: "@Atlas run smoke tests",
            cancellationToken: ct);

        Assert.Equal(2, harness.Deliveries.Count);
        var secondDelivery = harness.Deliveries[1];
        Assert.Equal(botB.Id, secondDelivery.BotId);
        Assert.Contains("<quoted_room_history>", secondDelivery.Prompt);
        Assert.Contains("@Sage check deployment", secondDelivery.Prompt);
        Assert.Contains("Deployment is green on staging.", secondDelivery.Prompt);
        Assert.Contains("@Atlas run smoke tests", secondDelivery.Prompt);
    }

    [Fact]
    public async Task UpdateGroupAsync_AddsNewMemberArchivesDepartedChatIdAndBumpsRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");
        var botC = harness.CreateBot("Nova");

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest("Squad", [botA.Id, botB.Id]));
        var originalAChatId = group.Members.Single(m => m.BotId == botA.Id).ChatId;
        var departedBChatId = group.Members.Single(m => m.BotId == botB.Id).ChatId;

        var updated = await harness.Groups.UpdateGroupAsync(
            new UpdateBotGroupRequest(group.Id, group.Revision, Name: "Squad v2", BotIds: [botA.Id, botC.Id]),
            ct);

        Assert.Equal("Squad v2", updated.Name);
        Assert.Equal(2, updated.Revision);
        Assert.Equal([botA.Id, botC.Id], updated.Members.Select(m => m.BotId).ToArray());
        Assert.Equal(originalAChatId, updated.Members.Single(m => m.BotId == botA.Id).ChatId);
        Assert.Contains(departedBChatId, updated.FormerChatIds);

        var newCChatId = updated.Members.Single(m => m.BotId == botC.Id).ChatId;
        var novaBot = harness.Bots.Bots.Single(b => b.Id == botC.Id);
        Assert.Contains(novaBot.Chats, c => c.Id == newCChatId && c.Kind == BotChatKind.GroupMember && c.GroupId == group.Id);

        // Stale revision fails.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Groups.UpdateGroupAsync(new UpdateBotGroupRequest(group.Id, Revision: 1, Name: "Stale"), ct));
    }

    [Fact]
    public void DeletingSingleBot_AutomaticallyRemovesFromGroupAndArchivesFormerChatId()
    {
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest(
            "Reviewers",
            [botA.Id, botB.Id],
            new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Bot, BotId = botB.Id }));
        var botBChatId = group.Members.Single(m => m.BotId == botB.Id).ChatId;

        harness.Bots.DeleteBot(botB.Id);

        var updated = harness.Groups.FindGroup(group.Id)!;
        Assert.Equal(2, updated.Revision);
        Assert.Single(updated.Members);
        Assert.Equal(botA.Id, updated.Members[0].BotId);
        Assert.Contains(botBChatId, updated.FormerChatIds);
        Assert.Equal(BotGroupReplyModeKind.Mentions, updated.ReplyMode.Kind);
    }

    [Fact]
    public async Task CreateThreadAsync_AllocatesFreshMemberChatsAutoTitlesFirstPostAndIsolatesMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest("Platform", [botA.Id, botB.Id]));
        var mainChatIds = group.Members.Select(m => m.ChatId).ToHashSet(StringComparer.Ordinal);

        var thread = await harness.Groups.CreateThreadAsync(group.Id, ct);

        Assert.Null(thread.Title);
        Assert.Equal(2, thread.Members.Count);
        Assert.All(thread.Members, m => Assert.DoesNotContain(m.ChatId, mainChatIds));

        var currentRev = harness.Groups.FindGroup(group.Id)!.Revision;
        await harness.Groups.SendMessageAsync(group.Id, currentRev, "@Sage main room note", threadId: null, cancellationToken: ct);

        var longFirstPost = "@Atlas " + new string('z', 100);
        await harness.Groups.SendMessageAsync(group.Id, currentRev, longFirstPost, threadId: thread.Id, cancellationToken: ct);
        await harness.Groups.SendMessageAsync(group.Id, currentRev, "@Sage second thread post", threadId: thread.Id, cancellationToken: ct);

        var updatedThread = harness.Groups.FindGroup(group.Id)!.Threads.Single(t => t.Id == thread.Id);
        Assert.NotNull(updatedThread.Title);
        Assert.Equal(80, updatedThread.Title!.Length);
        Assert.StartsWith("@Atlas zzz", updatedThread.Title, StringComparison.Ordinal);

        var mainPage = harness.Groups.ReadRoom(group.Id, threadId: null);
        var threadPage = harness.Groups.ReadRoom(group.Id, threadId: thread.Id);

        Assert.Single(mainPage.Messages);
        Assert.Equal("@Sage main room note", mainPage.Messages[0].Text);
        Assert.Equal(2, threadPage.Messages.Count);
        Assert.All(threadPage.Messages, m => Assert.Equal(thread.Id, m.ThreadId));
    }

    [Fact]
    public async Task StopAsync_AdvancesWatermarkMarksTurnsStoppedInvokesStopOverrideAndBlocksLateHandoffs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest("Release", [botA.Id, botB.Id]));

        // Send a message addressed to both bots.
        var msg = await harness.Groups.SendMessageAsync(group.Id, group.Revision, "@everyone start release", cancellationToken: ct);
        Assert.Equal(2, harness.Deliveries.Count);

        // Also seed a still-Queued turn on the same message to verify both Queued and Running turns become Stopped.
        harness.GroupStore.UpdateRoom(group.Id, room =>
        {
            var storedMsg = room.Messages.Single(m => m.Id == msg.Id);
            storedMsg.Turns.Add(new BotGroupTurn
            {
                OperationId = "queued-op",
                BotId = botB.Id,
                BotName = botB.Name,
                ChatId = group.Members.Single(m => m.BotId == botB.Id).ChatId,
                Status = BotGroupTurnStatus.Queued,
            });
            room.PendingDeliveries.Add(new BotGroupPendingDelivery
            {
                OperationId = "queued-op",
                MessageId = msg.Id,
                GroupId = group.Id,
            });
        });

        // Stop only Atlas first: its watermark advances, its turns become Stopped, StopOverride is invoked,
        // and a late handoff from Sage for rootSequence <= StoppedThroughSequence must be blocked.
        await harness.Groups.StopAsync(group.Id, threadId: null, botId: botB.Id, cancellationToken: ct);

        var updatedGroup = harness.Groups.FindGroup(group.Id)!;
        var atlasMember = updatedGroup.Members.Single(m => m.BotId == botB.Id);
        Assert.True(atlasMember.StoppedThroughSequence >= msg.Sequence);
        Assert.Contains(harness.Stopped, s => s.BotId == botB.Id && s.ChatId == atlasMember.ChatId);

        var roomAfterStopB = harness.GroupStore.ReadRoom(group.Id);
        Assert.Empty(roomAfterStopB.PendingDeliveries);
        var atlasTurns = roomAfterStopB.Messages.Single(m => m.Id == msg.Id).Turns.Where(t => t.BotId == botB.Id).ToList();
        Assert.All(atlasTurns, t => Assert.Equal(BotGroupTurnStatus.Stopped, t.Status));

        // Now Sage finishes its turn and tries to hand off to @Atlas on the stopped rootSequence.
        var sageDelivery = harness.Deliveries.Single(d => d.BotId == botA.Id);
        await harness.Groups.HandleTurnSettledAsync(
            sageDelivery.ChatId,
            sageDelivery.OperationId,
            "@Atlas please continue the release",
            cancelled: false,
            error: null);

        // Atlas must NOT be woken because rootSequence <= atlasMember.StoppedThroughSequence.
        Assert.Equal(2, harness.Deliveries.Count);
        Assert.DoesNotContain(harness.GroupStore.ReadRoom(group.Id).Messages, m => m.IsMentionHandoff);
    }

    [Fact]
    public async Task MentionHandoff_WakesMentionedBotMarksHandedOffWithoutDoubleWakeAndSkipsPausedBots()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");
        var botC = harness.CreateBot("Nova");
        harness.Bots.SetBotPaused(botC.Id, true);

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest("Triage", [botA.Id, botB.Id, botC.Id]));
        var rootMsg = await harness.Groups.SendMessageAsync(group.Id, group.Revision, "@Sage investigate incident", cancellationToken: ct);
        var sageDelivery = Assert.Single(harness.Deliveries);

        // Sage replies mentioning both Atlas (active) and Nova (paused).
        await harness.Groups.HandleTurnSettledAsync(
            sageDelivery.ChatId,
            sageDelivery.OperationId,
            "Root cause found. @Atlas please verify fix and @Nova notify stakeholders.",
            cancelled: false,
            error: null);

        Assert.Equal(2, harness.Deliveries.Count);
        var handoffDelivery = harness.Deliveries[1];
        Assert.Equal(botB.Id, handoffDelivery.BotId);
        Assert.Contains("[Handoff from @Sage]", handoffDelivery.Prompt);
        Assert.Contains("Original owner request: @Sage investigate incident", handoffDelivery.Prompt);
        Assert.Contains("@Atlas please verify fix", handoffDelivery.Prompt);

        var room = harness.GroupStore.ReadRoom(group.Id);
        var settledSageTurn = room.Messages.Single(m => m.Id == rootMsg.Id).Turns.Single();
        Assert.Equal([botB.Id], settledSageTurn.HandedOff);

        var handoffMsg = Assert.Single(room.Messages, m => m.IsMentionHandoff);
        Assert.Equal(1, handoffMsg.HopDepth);
        Assert.Equal(rootMsg.Id, handoffMsg.RootRequestId);
        Assert.Equal(rootMsg.Sequence, handoffMsg.RootSequence);
        Assert.Equal(botA.Id, handoffMsg.RequesterBotId);

        // Replaying settlement for the already-settled operation must not double-wake Atlas.
        await harness.Groups.HandleTurnSettledAsync(
            sageDelivery.ChatId,
            sageDelivery.OperationId,
            "@Atlas please verify fix",
            cancelled: false,
            error: null);

        Assert.Equal(2, harness.Deliveries.Count);
        Assert.Single(harness.GroupStore.ReadRoom(group.Id).Messages, m => m.IsMentionHandoff);
    }

    [Fact]
    public async Task HopLimitEnforcement_PausesAtMaxHopsAndContinueHandoffResumesCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest("PingPong", [botA.Id, botB.Id], MaxHops: 2));
        await harness.Groups.SendMessageAsync(group.Id, group.Revision, "@Sage kick off", cancellationToken: ct);

        // Hop 1: Sage -> Atlas (HopDepth = 1 <= 2)
        var d0 = harness.Deliveries[0];
        await harness.Groups.HandleTurnSettledAsync(d0.ChatId, d0.OperationId, "@Atlas hop 1", cancelled: false, error: null);
        Assert.Equal(2, harness.Deliveries.Count);
        Assert.Equal(botB.Id, harness.Deliveries[1].BotId);

        // Hop 2: Atlas -> Sage (HopDepth = 2 <= 2)
        var d1 = harness.Deliveries[1];
        await harness.Groups.HandleTurnSettledAsync(d1.ChatId, d1.OperationId, "@Sage hop 2", cancelled: false, error: null);
        Assert.Equal(3, harness.Deliveries.Count);
        Assert.Equal(botA.Id, harness.Deliveries[2].BotId);

        // Hop 3: Sage -> Atlas (HopDepth = 3 > 2) => paused by hop limit!
        var d2 = harness.Deliveries[2];
        await harness.Groups.HandleTurnSettledAsync(d2.ChatId, d2.OperationId, "@Atlas hop 3", cancelled: false, error: null);
        Assert.Equal(3, harness.Deliveries.Count);

        var room = harness.GroupStore.ReadRoom(group.Id);
        var pausedMsg = Assert.Single(room.Messages, m => m.PausedByHopLimit);
        Assert.Equal([botB.Id], pausedMsg.PausedTargetBotIds);
        Assert.Equal(d2.OperationId, pausedMsg.PausedSourceOperationId);

        // ContinueHandoffAsync resumes delivery cleanly to Atlas.
        var resumed = await harness.Groups.ContinueHandoffAsync(group.Id, pausedMsg.Id, extraHops: 3, cancellationToken: ct);
        Assert.True(resumed);
        Assert.Equal(4, harness.Deliveries.Count);
        Assert.Equal(botB.Id, harness.Deliveries[3].BotId);

        var refreshedPaused = harness.GroupStore.ReadRoom(group.Id).Messages.Single(m => m.Id == pausedMsg.Id);
        Assert.False(refreshedPaused.PausedByHopLimit);
        Assert.Null(refreshedPaused.PausedTargetBotIds);
    }

    [Fact]
    public async Task ResumePendingDeliveriesAsync_ReplaysPendingAndRepairsOrphanedRunningTurns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = CreateHarness();
        var botA = harness.CreateBot("Sage");
        var botB = harness.CreateBot("Atlas");

        var group = harness.Groups.CreateGroup(new CreateBotGroupRequest("CrashRecovery", [botA.Id, botB.Id]));
        var chatA = group.Members.Single(m => m.BotId == botA.Id).ChatId;
        var chatB = group.Members.Single(m => m.BotId == botB.Id).ChatId;

        // Seed state as if the process crashed: one orphaned Running turn and one undelivered Queued turn.
        harness.GroupStore.UpdateRoom(group.Id, room =>
        {
            room.NextSequence = 3;
            room.Messages.Add(new BotGroupMessage
            {
                Id = "msg-orphan",
                GroupId = group.Id,
                Sequence = 1,
                IsOwner = true,
                Text = "@Sage run build",
                Turns =
                [
                    new BotGroupTurn
                    {
                        OperationId = "op-orphan",
                        BotId = botA.Id,
                        BotName = botA.Name,
                        ChatId = chatA,
                        Status = BotGroupTurnStatus.Running,
                    },
                ],
            });
            room.Messages.Add(new BotGroupMessage
            {
                Id = "msg-pending",
                GroupId = group.Id,
                Sequence = 2,
                IsOwner = true,
                Text = "@Atlas verify tests",
                Turns =
                [
                    new BotGroupTurn
                    {
                        OperationId = "op-pending",
                        BotId = botB.Id,
                        BotName = botB.Name,
                        ChatId = chatB,
                        Status = BotGroupTurnStatus.Queued,
                    },
                ],
            });
            room.PendingDeliveries.Add(new BotGroupPendingDelivery
            {
                OperationId = "op-pending",
                MessageId = "msg-pending",
                GroupId = group.Id,
                ContextJson = "[]",
            });
        });

        // Construct a brand-new BotGroupService over the same persisted stores (simulating app restart).
        var replayed = new List<DeliveryRecord>();
        using var restarted = new BotGroupService(harness.GroupStore, harness.Bots, () => null)
        {
            DeliverOverride = (bot, chatId, opId, prompt) =>
            {
                replayed.Add(new DeliveryRecord(bot.Id, chatId, opId, prompt));
                return Task.FromResult(true);
            },
        };

        await restarted.ResumePendingDeliveriesAsync(ct);

        var room = harness.GroupStore.ReadRoom(group.Id);
        var orphanTurn = room.Messages.Single(m => m.Id == "msg-orphan").Turns.Single();
        Assert.Equal(BotGroupTurnStatus.Stopped, orphanTurn.Status);
        Assert.Equal("Interrupted before completion.", orphanTurn.Error);
        Assert.NotNull(orphanTurn.CompletedAt);

        var pendingTurn = room.Messages.Single(m => m.Id == "msg-pending").Turns.Single();
        Assert.Equal(BotGroupTurnStatus.Running, pendingTurn.Status);
        Assert.Empty(room.PendingDeliveries);

        var delivery = Assert.Single(replayed);
        Assert.Equal(botB.Id, delivery.BotId);
        Assert.Equal("op-pending", delivery.OperationId);
        Assert.Contains("@Atlas verify tests", delivery.Prompt);
    }

    // ---- helpers ----

    private RuntimeHarness CreateHarness()
    {
        var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new RuntimeHarness(dir);
    }

    private static List<string> ParseContextTexts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .EnumerateArray()
            .Select(el => el.GetProperty("text").GetString() ?? "")
            .ToList();
    }

    private sealed record DeliveryRecord(string BotId, string ChatId, string OperationId, string Prompt);

    private sealed class RuntimeHarness : IDisposable
    {
        public InMemoryBotStore BotStore { get; } = new();
        public InMemoryBotGroupStore GroupStore { get; } = new();
        public BotService Bots { get; }
        public BotGroupService Groups { get; }
        public List<DeliveryRecord> Deliveries { get; } = [];
        public List<(string BotId, string ChatId)> Stopped { get; } = [];

        public RuntimeHarness(string rootDir)
        {
            var paths = new FixedAppPaths(rootDir);
            var settings = new InMemorySettingsStore();
            var catalog = new AgentCatalog(paths);
            var scanner = new FakeAgentScanner("claude", "codex");
            var home = new BotHomeManager(new BotRootOptions(rootDir));

            Bots = new BotService(BotStore, catalog, scanner, settings, new PathComparer(ignoreCase: false), home);
            Groups = new BotGroupService(GroupStore, Bots, () => null)
            {
                DeliverOverride = (bot, chatId, opId, prompt) =>
                {
                    Deliveries.Add(new DeliveryRecord(bot.Id, chatId, opId, prompt));
                    return Task.FromResult(true);
                },
                StopOverride = (botId, chatId) =>
                {
                    Stopped.Add((botId, chatId));
                    return Task.CompletedTask;
                },
            };
        }

        public BotDefinition CreateBot(string name, string agentId = "claude") =>
            Bots.CreateBot(new NewBotRequest(name, agentId, "#5b5bd6", "blank", $"{name} role"));

        public void Dispose() => Groups.Dispose();
    }
}
