using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using Octopus.Remote.Protocol;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Xunit;

namespace Octopus.Remote.Tests;

public sealed class EchoMessage
{
    public string Text { get; set; } = "";
    public int Times { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EchoMessage))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

public class ProtocolTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly DeviceDescription Laptop = new("Laptop", DeviceKind.Desktop);
    private static readonly DeviceDescription Station = new("Station", DeviceKind.Desktop);

    /// <summary>A device and a host with a secure channel between them, over the given transports.</summary>
    private static async Task<(RemoteConnection Device, RemoteConnection Host)> Pair(IMessageTransport deviceSide, IMessageTransport hostSide)
    {
        using var device = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var accepting = SecureChannel.AcceptAsync(hostSide, host, Station, new Door { Paired = { device.PublicKey } });
        var channel = await SecureChannel.ConnectAsync(deviceSide, device, Laptop, PeerExpectation.Paired(host.PublicKey)).WaitAsync(Limit);
        return (new RemoteConnection(channel), new RemoteConnection(await accepting.WaitAsync(Limit)));
    }

    private static Task<(RemoteConnection Device, RemoteConnection Host)> Pair()
    {
        var (a, b) = InMemoryTransport.CreatePair();
        return Pair(a, b);
    }

    private static Task<EchoMessage> Echo(RemoteConnection connection, string method, EchoMessage message) =>
        connection.RequestAsync(method, message, TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage).WaitAsync(Limit);

    [Fact]
    public async Task A_request_gets_its_answer_and_both_sides_can_ask()
    {
        var (device, host) = await Pair();
        await using var _ = device;
        await using var __ = host;
        host.Handle("echo", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage,
            (message, _) => Task.FromResult(new EchoMessage { Text = string.Concat(Enumerable.Repeat(message.Text, message.Times)), Times = 1 }));
        device.Handle("whoami", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage,
            (_, _) => Task.FromResult(new EchoMessage { Text = "the device" }));
        device.Start();
        host.Start();

        Assert.Equal("终端终端终端", (await Echo(device, "echo", new EchoMessage { Text = "终端", Times = 3 })).Text);
        Assert.Equal("the device", (await Echo(host, "whoami", new EchoMessage())).Text);

        // Many at once come back each to its own caller.
        var answers = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Echo(device, "echo", new EchoMessage { Text = i.ToString(), Times = 1 })));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => i.ToString()), answers.Select(a => a.Text));
        Assert.Equal("Station", device.PeerDescription.Name);
    }

    [Fact]
    public async Task Errors_come_back_as_errors_with_their_code()
    {
        var (device, host) = await Pair();
        await using var _ = device;
        await using var __ = host;
        host.Handle("refuse", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage,
            (_, _) => throw new RemoteException(-33010, "No such session."));
        host.Handle("crash", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage,
            (_, _) => throw new InvalidOperationException("boom"));
        device.Start();
        host.Start();

        var refused = await Assert.ThrowsAsync<RemoteException>(() => Echo(device, "refuse", new EchoMessage()));
        Assert.Equal((-33010, "No such session."), (refused.Code, refused.Message));
        Assert.Equal(RemoteProtocol.InternalError, (await Assert.ThrowsAsync<RemoteException>(() => Echo(device, "crash", new EchoMessage()))).Code);
        Assert.Equal(RemoteProtocol.MethodNotFound, (await Assert.ThrowsAsync<RemoteException>(() => Echo(device, "nothing", new EchoMessage()))).Code);
        // The connection survives all of them.
        host.Handle("echo", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage, (message, _) => Task.FromResult(message));
        Assert.Equal("still here", (await Echo(device, "echo", new EchoMessage { Text = "still here" })).Text);
    }

    [Fact]
    public async Task Events_and_terminal_frames_arrive_in_the_order_they_were_sent()
    {
        var (device, host) = await Pair();
        await using var _ = device;
        await using var __ = host;
        var seen = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.On("session/title", TestJsonContext.Default.EchoMessage, message => { lock (seen) seen.Add("title:" + message.Text); });
        device.DataReceived = (header, payload) =>
        {
            lock (seen) seen.Add($"{header.Type}@{header.Channel}:{Encoding.UTF8.GetString(payload.Span)}");
            if (header.Type == FrameType.Record) done.TrySetResult();
        };
        device.Start();
        host.Start();

        await host.SendDataAsync(FrameType.Output, 7, "abc"u8);
        await host.NotifyAsync("session/title", new EchoMessage { Text = "vim" }, TestJsonContext.Default.EchoMessage);
        await host.NotifyAsync("unknown/event", new EchoMessage { Text = "ignored" }, TestJsonContext.Default.EchoMessage);
        await host.SendDataAsync(FrameType.Output, 7, "def"u8);
        await host.SendDataAsync(FrameType.Record, 9, "r"u8);
        await done.Task.WaitAsync(Limit);

        Assert.Equal(["Output@7:abc", "title:vim", "Output@7:def", "Record@9:r"], seen);
    }

    [Fact]
    public async Task When_the_connection_ends_waiting_callers_are_told()
    {
        var (device, host) = await Pair();
        var never = new TaskCompletionSource<EchoMessage>();
        host.Handle("slow", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage, (_, _) => never.Task);
        device.Start();
        host.Start();
        var waiting = Echo(device, "slow", new EchoMessage());

        await host.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => waiting);
        await device.Completion.WaitAsync(Limit);
        await Assert.ThrowsAnyAsync<IOException>(() => Echo(device, "slow", new EchoMessage()));
        await device.DisposeAsync();
    }

    [Fact]
    public async Task Hello_tells_each_side_what_the_other_can_do_and_refuses_another_version()
    {
        var (device, host) = await Pair();
        await using var _ = device;
        await using var __ = host;
        HelloMessage? fromDevice = null;
        var hostHello = new HelloMessage { AppVersion = "1.0.1200", Capabilities = ["terminal", "files", "git"], TerminalBuild = "ghostty-abc" };
        RemoteHello.Answer(host, hostHello, message => fromDevice = message);
        device.Start();
        host.Start();

        var deviceHello = new HelloMessage { AppVersion = "1.0.1100", Capabilities = ["terminal", "files", "push"], TerminalBuild = "ghostty-abc" };
        var fromHost = await RemoteHello.SendAsync(device, deviceHello).WaitAsync(Limit);

        Assert.Equal("1.0.1200", fromHost.AppVersion);
        Assert.Equal("1.0.1100", fromDevice!.AppVersion);
        Assert.Equal(["files", "terminal"], RemoteHello.Shared(deviceHello, fromHost).Order());
        Assert.Equal(fromHost.TerminalBuild, deviceHello.TerminalBuild);

        var old = await Assert.ThrowsAsync<RemoteException>(() => RemoteHello.SendAsync(device, new HelloMessage { Protocol = 0 }).WaitAsync(Limit));
        Assert.Equal(RemoteProtocol.VersionMismatch, old.Code);
        Assert.Contains("update Octopus on the device", old.Message);
        var newer = await Assert.ThrowsAsync<RemoteException>(() => RemoteHello.SendAsync(device, new HelloMessage { Protocol = 2 }).WaitAsync(Limit));
        Assert.Contains("update Octopus on the host", newer.Message);
    }

    [Fact]
    public void A_frame_header_keeps_its_type_flags_and_channel()
    {
        var message = new FrameHeader(FrameType.Output, FrameFlags.Droppable, 0xA1B2C3D4).Wrap("xyz"u8);

        Assert.Equal(FrameHeader.Length + 3, message.Length);
        Assert.Equal(new byte[] { 0x10, 0x01, 0xD4, 0xC3, 0xB2, 0xA1 }, message[..6]);
        Assert.True(FrameHeader.TryRead(message, out var header));
        Assert.Equal(new FrameHeader(FrameType.Output, FrameFlags.Droppable, 0xA1B2C3D4), header);
        Assert.False(FrameHeader.TryRead(new byte[5], out _));
        Assert.True(FrameHeader.IsKnown(FrameType.Ack));
        Assert.False(FrameHeader.IsKnown((FrameType)0x7F));
    }

    [Fact]
    public void A_pairing_link_survives_its_text_and_bad_text_is_not_a_link()
    {
        using var host = DeviceIdentity.Create();
        var link = new PairingLink(host.Id, host.PublicKey.Fingerprint(), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], "书房 Mac & PC", ["ws://192.168.1.5:47600", "relay:" + host.Id]);

        var text = link.ToString();
        Assert.StartsWith("octopus://pair?v=1&id=" + host.Id, text);
        var parsed = PairingLink.TryParse("  " + text + "\n")!;
        Assert.Equal(link.DeviceId, parsed.DeviceId);
        Assert.Equal(link.Fingerprint, parsed.Fingerprint);
        Assert.Equal(link.Token, parsed.Token);
        Assert.Equal("书房 Mac & PC", parsed.Name);
        Assert.Equal(link.Addresses, parsed.Addresses);

        Assert.Null(PairingLink.TryParse(null));
        Assert.Null(PairingLink.TryParse("https://example.com/pair?v=1"));
        Assert.Null(PairingLink.TryParse(text.Replace("v=1", "v=2")));
        Assert.Null(PairingLink.TryParse(text.Replace("&fp=", "&fp=!!")));
        Assert.Null(PairingLink.TryParse("octopus://pair?v=1&id=abc"));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_this_computer_and_private_networks_count_as_local(string address, bool local) =>
        Assert.Equal(local, LocalNetwork.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public async Task Over_a_real_websocket_a_device_reaches_the_host_and_they_talk()
    {
        await using var listener = new WebSocketListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepting = listener.AcceptAsync();
        var deviceSide = await WebSocketTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{listener.Port}")).WaitAsync(Limit);
        var accepted = await accepting.WaitAsync(Limit);
        Assert.True(IPAddress.IsLoopback(accepted.Remote!.Address));

        var (device, host) = await Pair(deviceSide, accepted.Transport);
        await using var _ = device;
        await using var __ = host;
        host.Handle("echo", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage, (message, _) => Task.FromResult(message));
        var output = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.DataReceived = (_, payload) => output.TrySetResult(payload.Length);
        device.Start();
        host.Start();

        Assert.Equal("over tcp", (await Echo(device, "echo", new EchoMessage { Text = "over tcp" })).Text);
        // A frame much larger than one read of the socket arrives whole.
        await host.SendDataAsync(FrameType.Output, 1, new byte[300_000]);
        Assert.Equal(300_000, await output.Task.WaitAsync(Limit));

        await device.DisposeAsync();
        await host.Completion.WaitAsync(Limit);
    }

    [Fact]
    public async Task The_listener_turns_away_what_is_not_an_upgrade_for_its_path()
    {
        await using var listener = new WebSocketListener(IPAddress.Loopback, 0);
        listener.Start();

        async Task<string> Raw(string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
            var buffer = new byte[256];
            var read = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(Limit);
            return Encoding.ASCII.GetString(buffer, 0, read);
        }

        Assert.StartsWith("HTTP/1.1 400", await Raw("GET / HTTP/1.1\r\nHost: x\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 400", await Raw("GET /octopus/v1 HTTP/1.1\r\nHost: x\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 400", await Raw("POST /octopus/v1 HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: abc\r\n\r\n"));
        // Another path, with everything else right.
        await Assert.ThrowsAnyAsync<Exception>(() => new ClientProbe().ConnectAsync(new Uri($"ws://127.0.0.1:{listener.Port}/other")));

        // None of them became a connection; a proper one still does.
        var accepting = listener.AcceptAsync();
        await using var good = await WebSocketTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{listener.Port}")).WaitAsync(Limit);
        await (await accepting.WaitAsync(Limit)).Transport.DisposeAsync();
    }

    [Fact]
    public async Task A_listener_that_allows_no_address_accepts_nobody()
    {
        await using var listener = new WebSocketListener(IPAddress.Loopback, 0) { AllowRemote = _ => false };
        listener.Start();

        await Assert.ThrowsAnyAsync<Exception>(() => WebSocketTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{listener.Port}")).WaitAsync(Limit));
    }

    [Fact]
    public async Task Workspace_messages_round_trip_with_source_generated_json_and_stay_compatible_with_older_payloads()
    {
        var (device, host) = await Pair();
        await using var _ = device;
        await using var __ = host;
        var json = RemoteJsonContext.Default;

        host.Handle(RemoteMethods.WorkspaceOptions, json.ProjectRefMessage, json.WorkspaceOptionsMessage,
            (req, _) => Task.FromResult(new WorkspaceOptionsMessage
            {
                IsGitRepository = true,
                DefaultBranch = "main",
                GhAvailable = true,
                Branches =
                [
                    new WorkspaceBranchMessage { Name = "main", IsCurrent = true, OccupiedBy = "Octopus" },
                    new WorkspaceBranchMessage { Name = "feat/mobile", IsRemote = false },
                ],
                Issues =
                [
                    new WorkspaceTaskOptionMessage
                    {
                        Kind = "issue", Key = "#142", Title = "Worktree support",
                        Url = "https://github.com/org/repo/issues/142", SuggestedBranch = "feat/142-worktree-support",
                    },
                ],
            }));
        host.Handle(RemoteMethods.WorkspaceCreate, json.CreateWorkspaceMessage, json.WorkspaceMessage,
            (req, _) => Task.FromResult(new WorkspaceMessage
            {
                Id = "ws-1",
                Name = req.BranchName,
                Branch = req.BranchName,
                Path = "/home/ada/.octopus/workspaces/Octopus/feat-142",
                TaskKind = req.TaskKind,
                TaskKey = req.TaskKey,
                TaskTitle = req.TaskTitle,
                TaskUrl = req.TaskUrl,
            }));
        device.Start();
        host.Start();

        var options = await device.RequestAsync(RemoteMethods.WorkspaceOptions, new ProjectRefMessage { ProjectId = "p1" },
            json.ProjectRefMessage, json.WorkspaceOptionsMessage).WaitAsync(Limit);
        Assert.True(options.IsGitRepository);
        Assert.Equal("main", options.DefaultBranch);
        Assert.True(options.GhAvailable);
        Assert.Equal(2, options.Branches.Count);
        Assert.Equal("#142", Assert.Single(options.Issues).Key);

        var created = await device.RequestAsync(RemoteMethods.WorkspaceCreate, new CreateWorkspaceMessage
        {
            ProjectId = "p1",
            CreateBranch = true,
            BranchName = "feat/142",
            BaseRef = "main",
            TaskKind = "issue",
            TaskKey = "#142",
            TaskTitle = "Worktree support",
            TaskUrl = "https://github.com/org/repo/issues/142",
        }, json.CreateWorkspaceMessage, json.WorkspaceMessage).WaitAsync(Limit);
        Assert.Equal(("ws-1", "feat/142", "#142"), (created.Id, created.Branch, created.TaskKey));

        // Older host payloads lacking workspace fields deserialize cleanly to defaults.
        var legacyProject = System.Text.Json.JsonSerializer.Deserialize("""{"id":"p1","name":"App","path":"/app"}""", json.ProjectMessage)!;
        Assert.False(legacyProject.IsGitRepository);
        Assert.Null(legacyProject.Workspaces);
        var legacyItem = System.Text.Json.JsonSerializer.Deserialize("""{"id":"i1","projectId":"p1","kind":"terminal","title":"zsh","activity":"idle","isRunning":false}""", json.ItemMessage)!;
        Assert.Null(legacyItem.WorkspaceId);
        Assert.Null(legacyItem.WorkspaceBranch);
    }

    private sealed class ClientProbe
    {
        public async Task ConnectAsync(Uri address)
        {
            using var client = new System.Net.WebSockets.ClientWebSocket();
            await client.ConnectAsync(address, CancellationToken.None).WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Protocol_v2_negotiates_version_compression_and_inlined_host_info_while_staying_compatible_with_v1()
    {
        // 1. v2 device <-> v2 host: negotiates version 2, Brotli compression, and inlined HostInfo.
        {
            var (device, host) = await Pair();
            await using var _ = device;
            await using var __ = host;
            var hostHello = new HelloMessage
            {
                Protocol = 1,
                MinProtocol = 1,
                MaxProtocol = 2,
                AppVersion = "1.0.1200",
                Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityProtocolV2, RemoteMethods.CapabilityCompression],
                Transport = TransportNegotiation.CreateDefault([new TransportOffer { Kind = TransportKinds.Quic, Address = "quic://127.0.0.1:47601", Priority = 100 }]),
                HostInfo = new HostInfoMessage { Name = "Studio", Os = "macos", AppVersion = "1.0.1200", Home = "/Users/ada" },
            };
            RemoteHello.Answer(host, hostHello);
            device.Start();
            host.Start();

            var deviceHello = new HelloMessage
            {
                Protocol = 1,
                MinProtocol = 1,
                MaxProtocol = 2,
                AppVersion = "1.0.1200",
                Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityProtocolV2, RemoteMethods.CapabilityCompression],
                Transport = TransportNegotiation.CreateDefault(),
            };
            var answer = await RemoteHello.SendAsync(device, deviceHello).WaitAsync(Limit);
            Assert.Equal(2, device.NegotiatedVersion);
            Assert.Equal(2, host.NegotiatedVersion);
            Assert.Equal(FrameCompressionKind.Brotli, device.Compression);
            Assert.Equal(FrameCompressionKind.Brotli, host.Compression);
            Assert.NotNull(answer.HostInfo);
            Assert.Equal("Studio", answer.HostInfo!.Name);
            Assert.Single(answer.Transport!.Transports!);
        }

        // 2. v2 device <-> v1 legacy host: seamlessly falls back to version 1 without error.
        {
            var (device, host) = await Pair();
            await using var _ = device;
            await using var __ = host;
            var v1HostHello = new HelloMessage { Protocol = 1, AppVersion = "1.0.1000", Capabilities = [RemoteMethods.CapabilityWorkspace] };
            RemoteHello.Answer(host, v1HostHello);
            device.Start();
            host.Start();

            var v2DeviceHello = new HelloMessage
            {
                Protocol = 1,
                MinProtocol = 1,
                MaxProtocol = 2,
                AppVersion = "1.0.1200",
                Transport = TransportNegotiation.CreateDefault(),
            };
            var answer = await RemoteHello.SendAsync(device, v2DeviceHello).WaitAsync(Limit);
            Assert.Equal(1, device.NegotiatedVersion);
            Assert.Equal(FrameCompressionKind.None, device.Compression);
            Assert.Null(answer.Transport);
        }
    }

    [Theory]
    [InlineData(FrameCompressionKind.Brotli)]
    [InlineData(FrameCompressionKind.Deflate)]
    public async Task Adaptive_frame_compression_shrinks_large_payloads_on_the_wire_and_leaves_small_ones_alone(FrameCompressionKind algorithm)
    {
        var (a, b) = InMemoryTransport.CreatePair();
        var wireLengths = new List<int>();
        a.Outgoing = bytes => { lock (wireLengths) wireLengths.Add(bytes.Length); return bytes; };
        var (device, host) = await Pair(a, b);
        await using var _ = device;
        await using var __ = host;

        device.ConfigureNegotiatedProtocol(2, algorithm, compressThreshold: 512);
        host.ConfigureNegotiatedProtocol(2, algorithm, compressThreshold: 512);

        host.Handle("echo", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage, (msg, _) => Task.FromResult(msg));
        var receivedData = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.DataReceived = (_, payload) => receivedData.TrySetResult(payload.ToArray());
        device.Start();
        host.Start();

        // Small message below threshold: not compressed.
        var small = await Echo(device, "echo", new EchoMessage { Text = "short", Times = 1 });
        Assert.Equal("short", small.Text);

        // Large repetitive payload: compressed significantly on the wire and restored bit-for-bit.
        var largeText = string.Concat(Enumerable.Repeat("Octopus terminal snapshot line with ANSI \u001b[32mgreen\u001b[0m text!\n", 400));
        var rawBytes = Encoding.UTF8.GetBytes(largeText);
        lock (wireLengths) wireLengths.Clear();
        await device.SendDataAsync(FrameType.Snapshot, 3, rawBytes);
        var arrived = await receivedData.Task.WaitAsync(Limit);
        Assert.Equal(rawBytes, arrived);
        int sentOnWire;
        lock (wireLengths) sentOnWire = wireLengths.Single();
        Assert.True(sentOnWire < rawBytes.Length / 4, $"Expected compressed wire size ({sentOnWire}) to be < 25% of raw ({rawBytes.Length}).");
    }

    [Fact]
    public async Task Ping_measures_round_trip_time_and_high_priority_frames_jump_ahead_of_bulk_frames()
    {
        var (a, b) = InMemoryTransport.CreatePair();
        var (device, host) = await Pair(a, b);
        await using var _ = device;
        await using var __ = host;

        var holdWire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockFirst = 1;
        a.Outgoing = bytes =>
        {
            if (Interlocked.Exchange(ref blockFirst, 0) == 1)
            {
                firstBlocked.TrySetResult();
                holdWire.Task.GetAwaiter().GetResult();
            }
            return bytes;
        };
        var order = new List<FrameType>();
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.DataReceived = (header, _) =>
        {
            lock (order)
            {
                order.Add(header.Type);
                if (order.Count == 3) allArrived.TrySetResult();
            }
        };
        device.Start();
        host.Start();

        // 1. Start a bulk send that blocks in the transport, then queue a second bulk send and a high-priority Input send.
        var bulk1 = Task.Run(async () => await device.SendDataAsync(FrameType.Output, 1, new byte[8192]).AsTask());
        await firstBlocked.Task.WaitAsync(Limit);
        var bulk2 = device.SendDataAsync(FrameType.Snapshot, 1, new byte[8192]).AsTask();
        var highInput = device.SendDataAsync(FrameType.Input, 1, "k"u8.ToArray()).AsTask();
        holdWire.TrySetResult();
        await Task.WhenAll(bulk1, bulk2, highInput, allArrived.Task).WaitAsync(Limit);

        // Input jumped ahead of the waiting Snapshot frame!
        Assert.Equal([FrameType.Output, FrameType.Input, FrameType.Snapshot], order);

        // 2. Ping / Pong measures RTT.
        var rtt = await device.PingAsync().WaitAsync(Limit);
        Assert.True(rtt > TimeSpan.Zero);
        Assert.NotNull(device.LastRtt);
    }

    [Fact]
    public async Task Stun_client_and_webrtc_p2p_transport_punch_udp_recover_lost_packets_and_migrate_live_connections()
    {
        // 1. RFC 5389 STUN Binding request/response round-trip for IPv4 and IPv6.
        var txId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var req = StunClient.BuildBindingRequest(txId);
        var mappedV4 = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 54321);
        var respV4 = StunClient.TryBuildBindingResponse(req, mappedV4)!;
        Assert.Equal(mappedV4, StunClient.TryParseBindingResponse(respV4, txId));

        var mappedV6 = new IPEndPoint(IPAddress.Parse("2001:db8::1234"), 61000);
        var respV6 = StunClient.TryBuildBindingResponse(req, mappedV6)!;
        Assert.Equal(mappedV6, StunClient.TryParseBindingResponse(respV6, txId));

        // 2. WebRTC P2P ICE hole punching + multi-fragment message reassembly + SACK packet loss recovery.
        var (p2pA, p2pB) = await WebRtcP2pTransport.CreateLoopbackPairAsync().WaitAsync(Limit);
        await using var _ = p2pA;
        await using var __ = p2pB;

        // Drop every 7th outgoing packet from p2pA to force SACK and retransmission.
        var packetCounter = 0;
        p2pA.OutgoingFilter = _ => Interlocked.Increment(ref packetCounter) % 7 != 0;

        // Establish a SecureChannel over the lossy UDP P2P transport and send a 100 KB multi-fragment message.
        var (device, host) = await Pair(p2pA, p2pB);
        await using var ___ = device;
        await using var ____ = host;
        host.Handle("echo", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage, (msg, _) => Task.FromResult(msg));
        device.Start();
        host.Start();

        var payload100K = new string('P', 100_000);
        var echoed = await Echo(device, "echo", new EchoMessage { Text = payload100K, Times = 1 });
        Assert.Equal(payload100K, echoed.Text);

        // 3. Live transport migration: start a connection over InMemoryTransport (e.g. Relay), then migrate live to a new SecureChannel!
        using var clientKey = DeviceIdentity.Create();
        using var hostKey = DeviceIdentity.Create();
        var (relayA, relayB) = InMemoryTransport.CreatePair();
        var accepting1 = SecureChannel.AcceptAsync(relayB, hostKey, Station, new Door { Paired = { clientKey.PublicKey } });
        var chClient1 = await SecureChannel.ConnectAsync(relayA, clientKey, Laptop, PeerExpectation.Paired(hostKey.PublicKey)).WaitAsync(Limit);
        var chHost1 = await accepting1.WaitAsync(Limit);

        await using var connClient = new RemoteConnection(chClient1);
        await using var connHost = new RemoteConnection(chHost1);
        connHost.Handle("echo", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage, (msg, _) => Task.FromResult(msg));
        connClient.Start();
        connHost.Start();

        Assert.Equal("before-migrate", (await Echo(connClient, "echo", new EchoMessage { Text = "before-migrate", Times = 1 })).Text);

        // Open a second transport pair (e.g. direct P2P or QUIC), authenticate with the same device keys, and migrate both ends live.
        var (directA, directB) = InMemoryTransport.CreatePair();
        var accepting2 = SecureChannel.AcceptAsync(directB, hostKey, Station, new Door { Paired = { clientKey.PublicKey } });
        var chClient2 = await SecureChannel.ConnectAsync(directA, clientKey, Laptop, PeerExpectation.Paired(hostKey.PublicKey)).WaitAsync(Limit);
        var chHost2 = await accepting2.WaitAsync(Limit);

        await connHost.MigrateTransportAsync(chHost2).WaitAsync(Limit);
        await connClient.MigrateTransportAsync(chClient2).WaitAsync(Limit);
        Assert.Equal(1, connClient.MigrationCount);
        Assert.Equal(1, connHost.MigrationCount);

        Assert.Equal("after-migrate", (await Echo(connClient, "echo", new EchoMessage { Text = "after-migrate", Times = 1 })).Text);
    }

    [Fact]
    public async Task Framed_stream_transport_and_transport_registry_support_custom_and_quic_schemes()
    {
        // 1. TransportRegistry resolves built-in and custom schemes.
        var registry = TransportRegistry.CreateDefault();
        Assert.True(registry.IsSupported("ws"));
        Assert.True(registry.IsSupported("wss"));
        Assert.NotNull(registry.Find("quic"));

        var (memA, memB) = InMemoryTransport.CreatePair();
        registry.Register(new CustomProvider("custom-p2p", memA));
        Assert.True(registry.IsSupported("custom-p2p"));
        var dialed = await registry.ConnectAsync(new Uri("custom-p2p://host-1"));
        Assert.Same(memA, dialed);
        await memB.DisposeAsync();

        // 2. FramedStreamTransport over full-duplex TCP socket streams (the framing layer used by QUIC / IPC / SSH stdio).
        using var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        var acceptTask = tcpListener.AcceptTcpClientAsync();
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Limit);
        using var tcpServer = await acceptTask.WaitAsync(Limit);

        await using var framedClient = new FramedStreamTransport(tcpClient.GetStream(), owner: tcpClient);
        await using var framedServer = new FramedStreamTransport(tcpServer.GetStream(), owner: tcpServer);
        var (device, host) = await Pair(framedClient, framedServer);
        await using var _ = device;
        await using var __ = host;
        host.Handle("echo", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage, (msg, _) => Task.FromResult(msg));
        device.Start();
        host.Start();

        var bigText = new string('Q', 200_000);
        Assert.Equal(bigText, (await Echo(device, "echo", new EchoMessage { Text = bigText, Times = 1 })).Text);

        // 3. If libmsquic is installed on this runner, also run a real QUIC listener + client exchange.
        if (QuicMessageTransport.IsSupported && QuicTransportListener.IsSupported)
        {
            await using var quicListener = new QuicTransportListener(IPAddress.Loopback, 0);
            await quicListener.StartAsync().WaitAsync(Limit);
            var quicAccepting = quicListener.AcceptAsync();
            var quicClientTransport = await QuicMessageTransport.ConnectAsync(new Uri($"quic://127.0.0.1:{quicListener.Port}")).WaitAsync(Limit);
            var quicAccepted = await quicAccepting.WaitAsync(Limit);
            var (qDevice, qHost) = await Pair(quicClientTransport, quicAccepted.Transport);
            await using var ___ = qDevice;
            await using var ____ = qHost;
            qHost.Handle("echo", TestJsonContext.Default.EchoMessage, TestJsonContext.Default.EchoMessage, (msg, _) => Task.FromResult(msg));
            qDevice.Start();
            qHost.Start();
            Assert.Equal("over-quic", (await Echo(qDevice, "echo", new EchoMessage { Text = "over-quic", Times = 1 })).Text);
        }
    }

    private sealed class CustomProvider(string scheme, IMessageTransport transport) : ITransportProvider
    {
        public string Scheme { get; } = scheme;
        public bool IsSupported => true;
        public Task<IMessageTransport> ConnectAsync(Uri address, CancellationToken cancellationToken = default) => Task.FromResult(transport);
    }
}
