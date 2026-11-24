using System.Text;
using System.Text.Json;
using Octopus.Acp.Transport;

namespace Octopus.Acp.Tests;

public class NdjsonLineFramerTests
{
    [Fact]
    public void Frames_split_chunks_crlf_and_routes_non_json_banners()
    {
        var framer = new NdjsonLineFramer();
        var messages = new List<string>();
        var others = new List<string>();
        var oversized = new List<OversizedAcpLine>();

        void Feed(string s) => framer.Feed(
            Encoding.UTF8.GetBytes(s),
            doc => { messages.Add(doc.RootElement.GetRawText()); doc.Dispose(); },
            others.Add,
            oversized.Add);

        Feed("Starting ACP server...\r\n");
        Feed("{\"jsonrpc\":\"2.0\",\"id\":1");
        Assert.Empty(messages);
        Feed(",\"result\":{\"ok\":true}}\r\n\n{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{}}\n");
        Feed("not-json-at-eof");
        framer.Complete(
            doc => { messages.Add(doc.RootElement.GetRawText()); doc.Dispose(); },
            others.Add,
            oversized.Add);

        Assert.Equal(2, messages.Count);
        Assert.Equal(["Starting ACP server...", "not-json-at-eof"], others);
        Assert.Empty(oversized);
    }

    [Fact]
    public void Drops_oversized_line_extracts_request_id_and_recovers_on_next_line()
    {
        var framer = new NdjsonLineFramer(maxLineBytes: 512);
        var messages = new List<string>();
        var others = new List<string>();
        var oversized = new List<OversizedAcpLine>();

        var big = "{\"jsonrpc\":\"2.0\",\"id\":42,\"result\":{\"blob\":\"" + new string('x', 1200) + "\"}}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":43,\"result\":{\"ok\":true}}\n";

        framer.Feed(
            Encoding.UTF8.GetBytes(big),
            doc => { messages.Add(doc.RootElement.GetRawText()); doc.Dispose(); },
            others.Add,
            oversized.Add);

        var dropped = Assert.Single(oversized);
        Assert.Equal("42", dropped.ExtractedRequestId);
        Assert.True(dropped.ByteCount > 1200);

        var recovered = Assert.Single(messages);
        using var parsed = JsonDocument.Parse(recovered);
        Assert.Equal(43, parsed.RootElement.GetProperty("id").GetInt32());
        Assert.Empty(others);
    }

    [Fact]
    public void Shrinks_buffer_after_large_line_and_handles_whitespace_padded_json()
    {
        var framer = new NdjsonLineFramer();
        var ids = new List<int>();
        var others = new List<string>();
        var oversized = new List<OversizedAcpLine>();

        var largePayload = "  {\"jsonrpc\":\"2.0\",\"id\":100,\"result\":{\"data\":\"" + new string('a', 100_000) + "\"}}  \r\n"
            + "   \t  \n"
            + "  {\"jsonrpc\":\"2.0\",\"id\":101,\"result\":{\"ok\":true}}\n";

        framer.Feed(
            Encoding.UTF8.GetBytes(largePayload),
            doc => { ids.Add(doc.RootElement.GetProperty("id").GetInt32()); doc.Dispose(); },
            others.Add,
            oversized.Add);

        Assert.Equal([100, 101], ids);
        Assert.Empty(others);
        Assert.Empty(oversized);
    }
}
