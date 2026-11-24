using System.Text.Json;
using Octopus.Agents.Subagents;

namespace Octopus.Agents.Tests;

public class AgySubagentTranscriptTests
{
    [Fact]
    public void Reads_parent_subagent_spawns_replies_and_errors_incrementally()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-agy-sub-").FullName;
        try
        {
            var parentId = "42664567-1010-4c5f-b316-e8701486150f";
            var sub1Id = "bf1142f9-15a0-43b6-a074-b3d2e0bacaab";
            var sub2Id = "0f41b9d7-1763-4b5a-b1dc-b3059bdcc8a1";
            var parentPath = AgySubagentTranscript.TranscriptPathFor(parentId, tempDir);
            Directory.CreateDirectory(Path.GetDirectoryName(parentPath)!);

            var sub1Path = AgySubagentTranscript.TranscriptPathFor(sub1Id, tempDir);
            var sub1Uri = new Uri(sub1Path).AbsoluteUri;
            var spawn1 = JsonSerializer.Serialize(new
            {
                type = "GENERIC",
                status = "DONE",
                content = $"Created At: 2026-11-24T17:25:19+08:00\nCreated the following subagent:\n{{\n  \"conversationId\": \"{sub1Id}\",\n  \"logAbsoluteUri\": \"{sub1Uri}\"\n}}\nThe subagent will send you a message.",
            });
            var bgTaskMsg = JsonSerializer.Serialize(new
            {
                type = "SYSTEM_MESSAGE",
                status = "DONE",
                content = $"<SYSTEM_MESSAGE>\n[Message] timestamp=2026-11-24T09:23:02Z sender={parentId}/task-7 priority=MESSAGE_PRIORITY_LOW content=Task finished\n</SYSTEM_MESSAGE>",
            });
            File.WriteAllText(parentPath, spawn1 + "\n" + bgTaskMsg + "\n");

            var delta1 = AgySubagentTranscript.ReadParentDelta(parentPath, 0, tempDir);
            var s1 = Assert.Single(delta1.Spawns);
            Assert.Equal(sub1Id, s1.ConversationId);
            Assert.Equal(sub1Path, s1.TranscriptPath);
            // Background shell task messages (<parentId>/task-7) must NOT be treated as subagent replies.
            Assert.Empty(delta1.Messages);

            // Append reply from sub1 and error from system for sub2.
            var reply1 = JsonSerializer.Serialize(new
            {
                type = "SYSTEM_MESSAGE",
                status = "DONE",
                content = $"The following is a <SYSTEM_MESSAGE>.\n\n<SYSTEM_MESSAGE>\n[Message] timestamp=2026-11-24T09:33:37Z sender={sub1Id} priority=MESSAGE_PRIORITY_HIGH content=All 60 tests passed.\n</SYSTEM_MESSAGE>",
            });
            var err2 = JsonSerializer.Serialize(new
            {
                type = "SYSTEM_MESSAGE",
                status = "DONE",
                content = $"<SYSTEM_MESSAGE>\n[Message] timestamp=2026-11-24T09:34:00Z sender=system priority=MESSAGE_PRIORITY_HIGH content=The subagent Worker (self) with id \"{sub2Id}\" encountered an error and has either stopped or failed: UNAUTHENTICATED\n</SYSTEM_MESSAGE>",
            });
            File.AppendAllText(parentPath, reply1 + "\n" + err2 + "\n");

            var delta2 = AgySubagentTranscript.ReadParentDelta(parentPath, delta1.ToOffset, tempDir);
            Assert.Empty(delta2.Spawns);
            Assert.Equal(2, delta2.Messages.Count);
            Assert.Equal(sub1Id, delta2.Messages[0].ConversationId);
            Assert.False(delta2.Messages[0].IsError);
            Assert.Equal("All 60 tests passed.", delta2.Messages[0].Content);
            Assert.Equal(sub2Id, delta2.Messages[1].ConversationId);
            Assert.True(delta2.Messages[1].IsError);
            Assert.Contains("UNAUTHENTICATED", delta2.Messages[1].Content);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Reads_child_subagent_tool_calls_with_json_quoted_args_and_completions()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-agy-child-").FullName;
        try
        {
            var childPath = AgySubagentTranscript.TranscriptPathFor("child-1", tempDir);
            Directory.CreateDirectory(Path.GetDirectoryName(childPath)!);

            var lines = new[]
            {
                """{"type":"PLANNER_RESPONSE","status":"DONE","tool_calls":[{"name":"run_command","args":{"CommandLine":"\"dotnet test\"","toolSummary":"\"Run unit tests\""}},{"name":"read_file","args":{"Path":"\"/repo/src/AcpMessages.cs\""}}],"content":""}""",
                """{"type":"GENERIC","status":"DONE","content":"Command exited with code 0."}""",
                """{"type":"GENERIC","status":"DONE","content":"file contents"}""",
                """{"type":"PLANNER_RESPONSE","status":"DONE","tool_calls":[{"name":"edit_file","args":{"Path":"\"/repo/src/HostConversations.cs\""}}],"content":""}""",
                """{"type":"GENERIC","status":"ERROR","content":"target not found"}""",
            };
            File.WriteAllLines(childPath, lines);

            var delta = AgySubagentTranscript.ReadChildDelta(childPath, 0);
            Assert.Equal(3, delta.ToolCalls.Count);
            Assert.Equal(("run_command", "execute", "Run unit tests"), (delta.ToolCalls[0].Name, delta.ToolCalls[0].Kind, delta.ToolCalls[0].Target));
            Assert.Equal(("read_file", "read", "AcpMessages.cs"), (delta.ToolCalls[1].Name, delta.ToolCalls[1].Kind, delta.ToolCalls[1].Target));
            Assert.Equal(("edit_file", "edit", "HostConversations.cs"), (delta.ToolCalls[2].Name, delta.ToolCalls[2].Kind, delta.ToolCalls[2].Target));
            Assert.Equal(2, delta.CompletedResults);
            Assert.Equal(1, delta.FailedResults);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }
}
