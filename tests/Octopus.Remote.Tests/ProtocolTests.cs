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
                Id = "ws-1", Name = req.BranchName, Branch = req.BranchName,
                Path = "/home/ada/.octopus/workspaces/Octopus/feat-142",
                TaskKind = req.TaskKind, TaskKey = req.TaskKey, TaskTitle = req.TaskTitle, TaskUrl = req.TaskUrl,
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
}
