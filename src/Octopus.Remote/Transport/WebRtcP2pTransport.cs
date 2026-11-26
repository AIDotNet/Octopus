using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Octopus.Remote.Protocol;

namespace Octopus.Remote.Transport;

/// <summary>
/// RFC 5389 STUN Binding client and responder helper for discovering server-reflexive (<c>srflx</c>) public UDP endpoints
/// during WebRTC P2P ICE candidate gathering (design/remote/传输协议v2与多传输优化.md §6.3).
/// </summary>
public static class StunClient
{
    public const uint MagicCookie = 0x2112A442;
    private const ushort BindingRequestType = 0x0001;
    private const ushort BindingResponseType = 0x0101;
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    /// <summary>Builds a 20-byte RFC 5389 STUN Binding Request with the given 12-byte transaction id.</summary>
    public static byte[] BuildBindingRequest(ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != 12) throw new ArgumentException("A STUN transaction id is 12 bytes.", nameof(transactionId));
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), BindingRequestType);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), MagicCookie);
        transactionId.CopyTo(packet.AsSpan(8, 12));
        return packet;
    }

    /// <summary>Builds an RFC 5389 STUN Binding Success Response encoding <paramref name="mapped"/> in <c>XOR-MAPPED-ADDRESS</c>.</summary>
    public static byte[]? TryBuildBindingResponse(ReadOnlySpan<byte> request, IPEndPoint mapped)
    {
        if (request.Length < 20) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(request[..2]) != BindingRequestType) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(request.Slice(4, 4)) != MagicCookie) return null;

        var ip = mapped.Address.IsIPv4MappedToIPv6 ? mapped.Address.MapToIPv4() : mapped.Address;
        var isV6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        var addrBytes = ip.GetAddressBytes();
        var attrValueLen = 4 + addrBytes.Length;
        var packet = new byte[20 + 4 + attrValueLen];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), BindingResponseType);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)(4 + attrValueLen));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), MagicCookie);
        request.Slice(8, 12).CopyTo(packet.AsSpan(8, 12));

        // Attribute header: XOR-MAPPED-ADDRESS (0x0020)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), AttrXorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), (ushort)attrValueLen);
        packet[24] = 0;
        packet[25] = isV6 ? (byte)0x02 : (byte)0x01;
        var xPort = (ushort)(mapped.Port ^ (int)(MagicCookie >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), xPort);
        for (var i = 0; i < addrBytes.Length; i++)
        {
            packet[28 + i] = (byte)(addrBytes[i] ^ packet[4 + i]);
        }
        return packet;
    }

    /// <summary>Parses a STUN Binding Response and returns the discovered <c>XOR-MAPPED-ADDRESS</c> (or <c>MAPPED-ADDRESS</c>).</summary>
    public static IPEndPoint? TryParseBindingResponse(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> expectedTransactionId)
    {
        if (packet.Length < 20 || expectedTransactionId.Length != 12) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[..2]) != BindingResponseType) return null;
        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4)) != MagicCookie) return null;
        if (!packet.Slice(8, 12).SequenceEqual(expectedTransactionId)) return null;
        if (20 + messageLength > packet.Length) return null;

        var offset = 20;
        var limit = 20 + messageLength;
        IPEndPoint? fallback = null;
        while (offset + 4 <= limit)
        {
            var attrType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
            var attrLen = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
            offset += 4;
            if (offset + attrLen > limit) break;
            var value = packet.Slice(offset, attrLen);
            if (attrType == AttrXorMappedAddress && TryReadAddress(value, packet.Slice(4, 16), xor: true) is { } xorEp)
            {
                return xorEp;
            }
            if (attrType == AttrMappedAddress && fallback is null)
            {
                fallback = TryReadAddress(value, default, xor: false);
            }
            // Attributes are padded to 4-byte boundaries.
            offset += (attrLen + 3) & ~3;
        }
        return fallback;
    }

    private static IPEndPoint? TryReadAddress(ReadOnlySpan<byte> value, ReadOnlySpan<byte> xorMask, bool xor)
    {
        if (value.Length < 8) return null;
        var family = value[1];
        var addrLen = family == 0x01 ? 4 : family == 0x02 ? 16 : 0;
        if (addrLen == 0 || value.Length < 4 + addrLen) return null;
        var rawPort = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));
        var port = xor ? (rawPort ^ (ushort)(MagicCookie >> 16)) : rawPort;
        Span<byte> addr = stackalloc byte[addrLen];
        for (var i = 0; i < addrLen; i++)
        {
            addr[i] = xor ? (byte)(value[4 + i] ^ xorMask[i]) : value[4 + i];
        }
        return new IPEndPoint(new IPAddress(addr), port);
    }

    /// <summary>Queries a STUN server over <paramref name="socket"/> to discover this socket's server-reflexive endpoint.</summary>
    public static async Task<IPEndPoint?> QueryAsync(Socket socket, IPEndPoint stunServer, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var txId = RandomNumberGenerator.GetBytes(12);
        var request = BuildBindingRequest(txId);
        await socket.SendToAsync(request, SocketFlags.None, stunServer, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[512];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        EndPoint remote = new IPEndPoint(stunServer.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cts.Token).ConfigureAwait(false);
                if (TryParseBindingResponse(buffer.AsSpan(0, received.ReceivedBytes), txId) is { } mapped)
                {
                    return mapped;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        return null;
    }
}

/// <summary>
/// Gathers ICE UDP candidates (host and optional STUN server-reflexive), exchanges credentials, and performs
/// authenticated bidirectional UDP hole punching to establish a <see cref="WebRtcP2pTransport"/>.
/// </summary>
public sealed class IceP2pEndpoint : IDisposable
{
    internal static readonly byte[] Magic = "OP2P"u8.ToArray();
    internal const byte KindProbe = 0x01;
    internal const byte KindProbeAck = 0x02;
    internal const byte KindData = 0x10;
    internal const byte KindAck = 0x11;
    internal const byte KindClose = 0x12;
    internal const byte KindKeepAlive = 0x13;
    private const int ProbeTagBytes = 16;

    private readonly Socket _socket;
    private bool _transferred;

    public IceP2pEndpoint(IPAddress? bindAddress = null, int port = 0)
    {
        var addr = bindAddress ?? IPAddress.Any;
        _socket = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(addr, port));
        Ufrag = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 8);
        Pwd = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 24);
    }

    public string Ufrag { get; }
    public string Pwd { get; }
    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    /// <summary>Builds the local <see cref="P2pHandshakeMessage"/> containing host candidates and optional STUN reflexive candidate.</summary>
    public async Task<P2pHandshakeMessage> GatherAsync(IPEndPoint? stunServer = null, bool includeLoopback = true, CancellationToken cancellationToken = default)
    {
        var candidates = new List<P2pCandidateMessage>();
        var bound = ((IPEndPoint)_socket.LocalEndPoint!).Address;
        var index = 1;

        if (!IPAddress.Any.Equals(bound) && !IPAddress.IPv6Any.Equals(bound))
        {
            candidates.Add(CreateCandidate(index++, bound, LocalPort, "host", priorityBase: 126));
        }
        else
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == _socket.AddressFamily && LocalNetwork.IsLocal(unicast.Address))
                        {
                            candidates.Add(CreateCandidate(index++, unicast.Address, LocalPort, "host", priorityBase: 126));
                        }
                    }
                }
            }
            catch (NetworkInformationException) { }

            if (includeLoopback || candidates.Count == 0)
            {
                var loopback = _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
                candidates.Add(CreateCandidate(index++, loopback, LocalPort, "host", priorityBase: 120));
            }
        }

        if (stunServer is not null)
        {
            var reflexive = await StunClient.QueryAsync(_socket, stunServer, TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            if (reflexive is not null)
            {
                candidates.Add(CreateCandidate(index++, reflexive.Address, reflexive.Port, "srflx", priorityBase: 100));
            }
        }

        return new P2pHandshakeMessage
        {
            Ufrag = Ufrag,
            Pwd = Pwd,
            Candidates = [.. candidates.OrderByDescending(c => c.Priority)],
        };
    }

    /// <summary>
    /// Runs bidirectional UDP hole punching and HMAC-SHA256 authenticated connectivity checks against
    /// <paramref name="remote"/>, returning a connected <see cref="WebRtcP2pTransport"/> once a candidate pair succeeds.
    /// </summary>
    public async Task<WebRtcP2pTransport> PunchAsync(P2pHandshakeMessage remote, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(remote.Ufrag) || string.IsNullOrEmpty(remote.Pwd) || remote.Candidates.Count == 0)
        {
            throw new IOException("The peer provided no valid WebRTC P2P ICE candidates.");
        }

        var endpoints = new List<IPEndPoint>();
        foreach (var candidate in remote.Candidates.OrderByDescending(c => c.Priority))
        {
            if (IPAddress.TryParse(candidate.Address, out var ip) && candidate.Port is > 0 and <= 65535 && ip.AddressFamily == _socket.AddressFamily)
            {
                var ep = new IPEndPoint(ip, candidate.Port);
                if (!endpoints.Contains(ep)) endpoints.Add(ep);
            }
        }
        if (endpoints.Count == 0) throw new IOException("No compatible UDP candidate endpoints were found.");

        var limit = timeout ?? TimeSpan.FromSeconds(5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);

        var probeOut = BuildProbePacket(KindProbe, Ufrag, remote.Ufrag, remote.Pwd);
        var buffer = new byte[1500];
        IPEndPoint? nominated = null;
        var verifiedIn = false;
        var verifiedOut = false;
        var bufferedData = new List<byte[]>();

        var nextProbeAt = 0L;
        while (!cts.IsCancellationRequested && !(verifiedIn && verifiedOut && nominated is not null))
        {
            var now = Environment.TickCount64;
            if (now >= nextProbeAt)
            {
                foreach (var target in endpoints)
                {
                    try { await _socket.SendToAsync(probeOut, SocketFlags.None, target, cts.Token).ConfigureAwait(false); }
                    catch (SocketException) { }
                }
                nextProbeAt = now + 40;
            }

            using var slice = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            slice.CancelAfter(TimeSpan.FromMilliseconds(40));
            try
            {
                EndPoint any = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, slice.Token).ConfigureAwait(false);
                var from = (IPEndPoint)result.RemoteEndPoint;
                var packet = buffer.AsSpan(0, result.ReceivedBytes);
                if (packet.Length < 5 || !packet[..4].SequenceEqual(Magic)) continue;
                var kind = packet[4];
                if (kind == KindProbe && VerifyProbePacket(packet, KindProbe, remote.Ufrag, Ufrag, Pwd))
                {
                    nominated ??= from;
                    verifiedIn = true;
                    var ack = BuildProbePacket(KindProbeAck, Ufrag, remote.Ufrag, remote.Pwd);
                    await _socket.SendToAsync(ack, SocketFlags.None, from, cts.Token).ConfigureAwait(false);
                }
                else if (kind == KindProbeAck && VerifyProbePacket(packet, KindProbeAck, remote.Ufrag, Ufrag, Pwd))
                {
                    nominated = from;
                    verifiedOut = true;
                }
                else if (kind == KindData && verifiedIn && nominated is not null)
                {
                    // The peer already transitioned to data frames on the punched path.
                    verifiedOut = true;
                    bufferedData.Add(packet.ToArray());
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
            }
        }

        if (nominated is null || !verifiedIn || !verifiedOut)
        {
            throw new IOException("WebRTC P2P UDP hole punching timed out.");
        }

        // Send one final ProbeAck so a peer still waiting on its outbound check completes immediately.
        var finalAck = BuildProbePacket(KindProbeAck, Ufrag, remote.Ufrag, remote.Pwd);
        try { await _socket.SendToAsync(finalAck, SocketFlags.None, nominated, CancellationToken.None).ConfigureAwait(false); }
        catch { }

        _transferred = true;
        return new WebRtcP2pTransport(_socket, nominated, bufferedData);
    }

    private static P2pCandidateMessage CreateCandidate(int index, IPAddress address, int port, string type, uint priorityBase) => new()
    {
        Foundation = index.ToString(),
        Component = 1,
        Transport = "udp",
        Type = type,
        Address = address.ToString(),
        Port = port,
        Priority = (priorityBase << 24) | (65535u << 8) | 255u,
    };

    private static byte[] BuildProbePacket(byte kind, string senderUfrag, string receiverUfrag, string targetPwd)
    {
        var label = Encoding.ASCII.GetBytes($"{senderUfrag}:{receiverUfrag}");
        var packet = new byte[5 + 2 + label.Length + ProbeTagBytes];
        Magic.CopyTo(packet, 0);
        packet[4] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), (ushort)label.Length);
        label.CopyTo(packet.AsSpan(7));
        var mac = HMACSHA256.HashData(Encoding.ASCII.GetBytes(targetPwd), packet.AsSpan(0, 7 + label.Length));
        mac.AsSpan(0, ProbeTagBytes).CopyTo(packet.AsSpan(7 + label.Length));
        return packet;
    }

    private static bool VerifyProbePacket(ReadOnlySpan<byte> packet, byte expectedKind, string expectedSenderUfrag, string expectedReceiverUfrag, string ownPwd)
    {
        if (packet.Length < 7 + ProbeTagBytes || packet[4] != expectedKind) return false;
        var labelLen = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(5, 2));
        if (packet.Length != 7 + labelLen + ProbeTagBytes) return false;
        var expectedLabel = Encoding.ASCII.GetBytes($"{expectedSenderUfrag}:{expectedReceiverUfrag}");
        if (!packet.Slice(7, labelLen).SequenceEqual(expectedLabel)) return false;
        var mac = HMACSHA256.HashData(Encoding.ASCII.GetBytes(ownPwd), packet[..(7 + labelLen)]);
        return CryptographicOperations.FixedTimeEquals(mac.AsSpan(0, ProbeTagBytes), packet.Slice(7 + labelLen, ProbeTagBytes));
    }

    public void Dispose()
    {
        if (!_transferred) _socket.Dispose();
    }
}

/// <summary>
/// Reliable, ordered, SACK-driven UDP datagram transport providing WebRTC DataChannel semantics over a hole-punched
/// UDP socket (design/remote/传输协议v2与多传输优化.md §6.3). Splits messages up to 1 MiB into 1200-byte MTU-safe fragments,
/// reassembles them in order, and recovers lost packets with selective acknowledgements and fast retransmit.
/// </summary>
public sealed class WebRtcP2pTransport : IMessageTransport
{
    public const int MaxFragmentPayload = 1200;
    public const int WindowSizePackets = 64;
    private const int DataHeaderBytes = 16;
    private const int AckPacketBytes = 21;

    private readonly Socket _socket;
    private IPEndPoint _remote;
    private readonly Channel<byte[]> _readyMessages = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly SortedDictionary<uint, SentPacket> _inflight = new();
    private readonly SortedDictionary<uint, ReceivedFragment> _outOfOrder = new();
    private readonly Dictionary<ushort, MessageReassembler> _reassemblers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiveLoop;
    private readonly Task _timerLoop;

    private TaskCompletionSource? _windowSignal;
    private uint _nextSendSeq = 1;
    private ushort _nextSendMsgId = 1;
    private uint _nextExpectedRecvSeq = 1;
    private ushort _nextExpectedRecvMsgId = 1;
    private readonly SortedDictionary<ushort, byte[]> _completedOutOfOrderMessages = new();
    private int _rtoMs = 60;
    private int _disposed;

    private sealed class SentPacket(uint seq, byte[] datagram)
    {
        public uint Seq { get; } = seq;
        public byte[] Datagram { get; } = datagram;
        public long SentAtMs { get; set; } = Environment.TickCount64;
        public int MissingAckReports { get; set; }
    }

    private readonly record struct ReceivedFragment(ushort MsgId, ushort FragIndex, ushort FragCount, byte[] Payload);

    private sealed class MessageReassembler(ushort fragCount)
    {
        private readonly byte[]?[] _pieces = new byte[fragCount][];
        private int _received;
        private int _totalBytes;

        public ushort FragCount { get; } = fragCount;

        public bool Add(ushort index, byte[] piece, out byte[]? whole)
        {
            whole = null;
            if (index >= FragCount || _pieces[index] is not null) return false;
            _pieces[index] = piece;
            _received++;
            _totalBytes += piece.Length;
            if (_received < FragCount) return false;

            var result = new byte[_totalBytes];
            var offset = 0;
            foreach (var part in _pieces)
            {
                part!.CopyTo(result.AsSpan(offset));
                offset += part!.Length;
            }
            whole = result;
            return true;
        }
    }

    internal WebRtcP2pTransport(Socket socket, IPEndPoint remote, IReadOnlyList<byte[]>? initialPackets = null)
    {
        _socket = socket;
        _remote = remote;
        if (initialPackets is { Count: > 0 })
        {
            foreach (var packet in initialPackets) ProcessIncomingPacket(packet);
        }
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _timerLoop = Task.Run(RetransmitAndKeepAliveLoopAsync);
    }

    /// <summary>Creates a connected localhost pair of <see cref="WebRtcP2pTransport"/> sockets for direct testing.</summary>
    public static async Task<(WebRtcP2pTransport A, WebRtcP2pTransport B)> CreateLoopbackPairAsync(CancellationToken cancellationToken = default)
    {
        using var epA = new IceP2pEndpoint(IPAddress.Loopback, 0);
        using var epB = new IceP2pEndpoint(IPAddress.Loopback, 0);
        var hsA = await epA.GatherAsync(includeLoopback: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        var hsB = await epB.GatherAsync(includeLoopback: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        var taskA = epA.PunchAsync(hsB, cancellationToken: cancellationToken);
        var taskB = epB.PunchAsync(hsA, cancellationToken: cancellationToken);
        await Task.WhenAll(taskA, taskB).ConfigureAwait(false);
        return (await taskA.ConfigureAwait(false), await taskB.ConfigureAwait(false));
    }

    /// <summary>Test hook: drops outgoing datagrams when returning false, to verify SACK and retransmission under packet loss.</summary>
    public Func<ReadOnlyMemory<byte>, bool>? OutgoingFilter { get; set; }

    public IPEndPoint RemoteEndPoint => _remote;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new IOException("The P2P transport is closed.");
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fragCount = (ushort)Math.Max(1, (message.Length + MaxFragmentPayload - 1) / MaxFragmentPayload);
            ushort msgId;
            lock (_stateLock)
            {
                msgId = _nextSendMsgId++;
            }

            var offset = 0;
            for (ushort fragIndex = 0; fragIndex < fragCount; fragIndex++)
            {
                var length = Math.Min(MaxFragmentPayload, message.Length - offset);
                var slice = message.Slice(offset, length);
                offset += length;

                SentPacket packet;
                while (true)
                {
                    Task? waitWindow = null;
                    lock (_stateLock)
                    {
                        if (_disposed != 0) throw new IOException("The P2P transport is closed.");
                        if (_inflight.Count < WindowSizePackets)
                        {
                            var seq = _nextSendSeq++;
                            var datagram = BuildDataDatagram(msgId, fragIndex, fragCount, seq, slice.Span);
                            packet = new SentPacket(seq, datagram);
                            _inflight[seq] = packet;
                            break;
                        }
                        _windowSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        waitWindow = _windowSignal.Task;
                    }
                    await waitWindow.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await SendDatagramAsync(packet.Datagram, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await _readyMessages.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (message.Length > maxLength)
            {
                throw new IOException($"A P2P message of {message.Length} bytes exceeds the limit of {maxLength}.");
            }
            return message;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private static byte[] BuildDataDatagram(ushort msgId, ushort fragIndex, ushort fragCount, uint seq, ReadOnlySpan<byte> payload)
    {
        var datagram = new byte[DataHeaderBytes + payload.Length];
        IceP2pEndpoint.Magic.CopyTo(datagram, 0);
        datagram[4] = IceP2pEndpoint.KindData;
        datagram[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(6, 2), msgId);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(8, 2), fragIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(10, 2), fragCount);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(12, 4), seq);
        payload.CopyTo(datagram.AsSpan(DataHeaderBytes));
        return datagram;
    }

    private byte[] BuildAckDatagramLocked()
    {
        var ack = new byte[AckPacketBytes];
        IceP2pEndpoint.Magic.CopyTo(ack, 0);
        ack[4] = IceP2pEndpoint.KindAck;
        var cumulative = _nextExpectedRecvSeq - 1;
        var sackBase = _nextExpectedRecvSeq;
        ulong bitmap = 0;
        foreach (var seq in _outOfOrder.Keys)
        {
            if (seq >= sackBase && seq - sackBase < 64)
            {
                bitmap |= 1UL << (int)(seq - sackBase);
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(5, 4), cumulative);
        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(9, 4), sackBase);
        BinaryPrimitives.WriteUInt64LittleEndian(ack.AsSpan(13, 8), bitmap);
        return ack;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    break;
                }
                _remote = (IPEndPoint)result.RemoteEndPoint;
                var ackToSend = ProcessIncomingPacket(buffer.AsSpan(0, result.ReceivedBytes));
                if (ackToSend is not null)
                {
                    await SendDatagramAsync(ackToSend, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _readyMessages.Writer.TryComplete();
        }
    }

    private byte[]? ProcessIncomingPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 5 || !packet[..4].SequenceEqual(IceP2pEndpoint.Magic)) return null;
        var kind = packet[4];
        switch (kind)
        {
            case IceP2pEndpoint.KindData when packet.Length >= DataHeaderBytes:
            {
                var msgId = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(6, 2));
                var fragIndex = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(8, 2));
                var fragCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2));
                var seq = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, 4));
                if (fragCount == 0 || fragIndex >= fragCount) return null;
                var payload = packet[DataHeaderBytes..].ToArray();

                lock (_stateLock)
                {
                    if (seq >= _nextExpectedRecvSeq && !_outOfOrder.ContainsKey(seq))
                    {
                        _outOfOrder[seq] = new ReceivedFragment(msgId, fragIndex, fragCount, payload);
                        while (_outOfOrder.Remove(_nextExpectedRecvSeq, out var nextFrag))
                        {
                            _nextExpectedRecvSeq++;
                            DeliverFragmentLocked(nextFrag);
                        }
                    }
                    return BuildAckDatagramLocked();
                }
            }

            case IceP2pEndpoint.KindAck when packet.Length >= AckPacketBytes:
            {
                var cumulative = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(5, 4));
                var sackBase = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(9, 4));
                var bitmap = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(13, 8));

                List<byte[]>? fastRetransmit = null;
                TaskCompletionSource? wakeWindow = null;
                var now = Environment.TickCount64;
                lock (_stateLock)
                {
                    var keys = _inflight.Keys.ToList();
                    var highestSacked = cumulative;
                    foreach (var seq in keys)
                    {
                        var sacked = seq >= sackBase && seq - sackBase < 64 && (bitmap & (1UL << (int)(seq - sackBase))) != 0;
                        if (seq <= cumulative || sacked)
                        {
                            if (_inflight.Remove(seq, out var acked) && acked.MissingAckReports == 0)
                            {
                                var sample = (int)Math.Clamp(now - acked.SentAtMs, 5, 1000);
                                _rtoMs = Math.Clamp((_rtoMs * 3 + sample * 2) / 4, 25, 500);
                            }
                            if (seq > highestSacked) highestSacked = seq;
                        }
                    }
                    foreach (var (_, pending) in _inflight)
                    {
                        if (pending.Seq < highestSacked)
                        {
                            pending.MissingAckReports++;
                            if (pending.MissingAckReports == 2)
                            {
                                fastRetransmit ??= [];
                                fastRetransmit.Add(pending.Datagram);
                                pending.SentAtMs = now;
                            }
                        }
                    }
                    if (_inflight.Count < WindowSizePackets && _windowSignal is not null)
                    {
                        wakeWindow = _windowSignal;
                        _windowSignal = null;
                    }
                }
                wakeWindow?.TrySetResult();
                if (fastRetransmit is not null)
                {
                    foreach (var dgram in fastRetransmit) _ = SendDatagramAsync(dgram, CancellationToken.None).AsTask();
                }
                return null;
            }

            case IceP2pEndpoint.KindClose:
                _readyMessages.Writer.TryComplete();
                _stop.Cancel();
                return null;

            default:
                return null;
        }
    }

    private void DeliverFragmentLocked(ReceivedFragment fragment)
    {
        if (!_reassemblers.TryGetValue(fragment.MsgId, out var reassembler))
        {
            reassembler = new MessageReassembler(fragment.FragCount);
            _reassemblers[fragment.MsgId] = reassembler;
        }
        if (reassembler.Add(fragment.FragIndex, fragment.Payload, out var whole))
        {
            _reassemblers.Remove(fragment.MsgId);
            _completedOutOfOrderMessages[fragment.MsgId] = whole!;
            while (_completedOutOfOrderMessages.Remove(_nextExpectedRecvMsgId, out var ordered))
            {
                _nextExpectedRecvMsgId++;
                _readyMessages.Writer.TryWrite(ordered);
            }
        }
    }

    private async Task RetransmitAndKeepAliveLoopAsync()
    {
        var lastKeepAlive = Environment.TickCount64;
        var keepAlivePacket = new byte[] { (byte)'O', (byte)'P', (byte)'2', (byte)'P', IceP2pEndpoint.KindKeepAlive };
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(20, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = Environment.TickCount64;
            List<byte[]>? expired = null;
            lock (_stateLock)
            {
                foreach (var (_, packet) in _inflight)
                {
                    if (now - packet.SentAtMs >= _rtoMs)
                    {
                        expired ??= [];
                        expired.Add(packet.Datagram);
                        packet.SentAtMs = now;
                        if (expired.Count >= 8) break;
                    }
                }
            }
            if (expired is not null)
            {
                foreach (var dgram in expired) await SendDatagramAsync(dgram, CancellationToken.None).ConfigureAwait(false);
                lastKeepAlive = now;
            }
            else if (now - lastKeepAlive >= 10_000)
            {
                await SendDatagramAsync(keepAlivePacket, CancellationToken.None).ConfigureAwait(false);
                lastKeepAlive = now;
            }
        }
    }

    private async ValueTask SendDatagramAsync(byte[] datagram, CancellationToken cancellationToken)
    {
        if (OutgoingFilter is { } filter && !filter(datagram)) return;
        try
        {
            await _socket.SendToAsync(datagram, SocketFlags.None, _remote, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var closePacket = new byte[] { (byte)'O', (byte)'P', (byte)'2', (byte)'P', IceP2pEndpoint.KindClose };
        try { await _socket.SendToAsync(closePacket, SocketFlags.None, _remote, CancellationToken.None).ConfigureAwait(false); }
        catch { }
        await _stop.CancelAsync().ConfigureAwait(false);
        TaskCompletionSource? wake;
        lock (_stateLock)
        {
            wake = _windowSignal;
            _windowSignal = null;
        }
        wake?.TrySetCanceled();
        _socket.Dispose();
        try { await Task.WhenAll(_receiveLoop, _timerLoop).ConfigureAwait(false); } catch { }
        _readyMessages.Writer.TryComplete();
        _stop.Dispose();
    }
}
