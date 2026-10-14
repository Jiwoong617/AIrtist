using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

// One independent process participant; the host relays peer packets with pinned sender IDs.
internal sealed class LocalRoomConnection : IOnlineConnection
{
    private readonly string hash, code;
    private readonly bool host;
    private readonly int capacity;
    private readonly Func<bool>? acceptLocal;
    private readonly TcpListener? listener;
    private readonly LocalRoomAddress address;
    private readonly List<Peer> peers = new();
    private readonly Queue<RoundInboundPacket> packets = new();
    private readonly HashSet<string> prepared = new(), receipts = new();
    private readonly TcpClient? joining;
    private readonly Task? connecting;
    private LocalWire? upstream;
    private ConnectionMember[] members;
    private string localId, hostId;
    private readonly Guid scope;
    private long revision = 1, preparationStarted, created = Stopwatch.GetTimestamp();
    private string stage = "waiting", message = "방 참가 완료";
    private bool ownPrepared, handed, failed, left, disposed;
    internal int ParticipantCount => members.Length;
    internal string Code => code;
    private sealed class Peer(LocalWire wire)
    {
        internal readonly LocalWire Wire = wire;
        internal readonly long Created = Stopwatch.GetTimestamp();
        internal ConnectionMember? Member;
    }
    internal static LocalRoomConnection Host(string hash, int capacity, Func<bool> acceptLocal)
        => new(hash, capacity, acceptLocal);
    private LocalRoomConnection(string hash, int capacity, Func<bool> acceptLocal)
    {
        if (capacity is < 2 or > 16) throw new ArgumentException("방 정원을 확인하세요.");
        this.hash = hash; this.capacity = capacity; this.acceptLocal = acceptLocal; host = true;
        localId = hostId = Guid.NewGuid().ToString("N"); scope = Guid.NewGuid();
        members = [new(localId, Guid.NewGuid())]; code = RoomCode.FromLobbyId(RoomCode.CreateLobbyId())!;
        listener = new(IPAddress.Loopback, 0); listener.Start(16);
        try { address = LocalRoomAddress.Register(code, ((IPEndPoint)listener.LocalEndpoint).Port, hash); }
        catch { listener.Stop(); throw; }
    }
    internal LocalRoomConnection(string hash, LocalRoomAddress address)
    {
        if (hash != address.Hash) throw new ArgumentException("게임 버전이 일치하지 않습니다.");
        this.hash = hash; this.address = address; code = address.Code; capacity = 16;
        localId = Guid.NewGuid().ToString("N"); hostId = ""; members = []; scope = Guid.Empty;
        joining = new TcpClient(AddressFamily.InterNetwork);
        connecting = joining.ConnectAsync(IPAddress.Loopback, address.Port);
        message = "방 참가 중";
    }
    private Guid activeScope;
    public EosConnectionSnapshot Authentication => new(EosConnectionState.Connected, localId, "연결 완료");
    public EosLobbySnapshot Lobby
    {
        get
        {
            var state = left ? EosLobbyState.Idle : failed ? EosLobbyState.Failed : members.Length == 0 ? EosLobbyState.Joining : EosLobbyState.Joined;
            var listing = new EosLobbyListing(RoomCode.ToLobbyId(code), hostId, hash, host ? capacity : remoteCapacity, (host ? capacity : remoteCapacity) - members.Length);
            return new(state, [], state == EosLobbyState.Joined ? new(listing, Array.AsReadOnly(members.Select(m => m.PlayerId).ToArray())) : null, revision, message);
        }
    }
    private int remoteCapacity = 16;
    public LobbyBootstrapSnapshot? Bootstrap => !ownPrepared ? null : new(failed ? LobbyBootstrapState.Failed : handed ? LobbyBootstrapState.HandedOff :
        stage == "run" ? LobbyBootstrapState.Ready : LobbyBootstrapState.Preparing, host ? scope : activeScope, prepared.Count, members.Length, "참가자 준비 중");
    public void Tick()
    {
        if (disposed || failed || left) return;
        if (host) TickHost(); else TickGuest();
        if (ownPrepared && stage != "run" && Stopwatch.GetElapsedTime(preparationStarted) > TimeSpan.FromSeconds(60)) Fail();
    }
    private void TickHost()
    {
        for (int i = 0; i < 4 && listener!.Pending(); i++)
        {
            var socket = listener.AcceptTcpClient();
            if (peers.Count >= 20 || stage != "waiting") { socket.Dispose(); continue; }
            peers.Add(new(new LocalWire(socket)));
        }
        foreach (var peer in peers.ToArray())
        {
            peer.Wire.Tick();
            for (int i = 0; i < 64 && peer.Wire.Receive(out var data); i++) HandleHost(peer, data);
            if (peer.Member is null && Stopwatch.GetElapsedTime(peer.Created) > TimeSpan.FromSeconds(5)) peer.Wire.Dispose();
            if (!peer.Wire.Closed) continue;
            peers.Remove(peer); peer.Wire.Dispose();
            if (peer.Member is null) continue;
            if (stage != "waiting") { Fail(); return; }
            members = members.Where(m => m.PlayerId != peer.Member.PlayerId).ToArray(); revision++; Broadcast();
        }
    }
    private void HandleHost(Peer peer, LocalMessage data)
    {
        if (failed || peer.Wire.Closed) return;
        if (data.Kind == "ping") return;
        if (peer.Member is null)
        {
            if (data.Kind != "hello" || data.Code != code || data.Token != address.Token || data.Hash != hash ||
                members.Length >= capacity || stage != "waiting" || acceptLocal?.Invoke() != true) { peer.Wire.Dispose(); return; }
            peer.Member = new(Guid.NewGuid().ToString("N"), Guid.NewGuid());
            members = members.Append(peer.Member).OrderBy(m => m.PlayerId, StringComparer.Ordinal).ToArray(); revision++; Broadcast(); return;
        }
        switch (data.Kind)
        {
            case "prepare" when data.Revision == revision && stage is "waiting" or "prepare":
                BeginPreparation(); prepared.Add(peer.Member.PlayerId); AdvanceBarrier(); break;
            case "ack" when data.Scope == scope && data.Revision == revision && stage is "offer" or "run":
                if (stage == "offer" && receipts.Add(peer.Member.PlayerId)) AdvanceBarrier(); break;
            case "packet" when stage == "run" && ValidPacket(data) && data.Id is null:
                Route(peer.Member.PlayerId, data.Target!, data.PacketKind, data.Bytes!); break;
            default: peer.Wire.Dispose(); break;
        }
    }
    private void TickGuest()
    {
        if (upstream is null)
        {
            if (!connecting!.IsCompleted) { if (Stopwatch.GetElapsedTime(created) > TimeSpan.FromSeconds(5)) Fail(); return; }
            try { connecting.GetAwaiter().GetResult(); upstream = new LocalWire(joining!); }
            catch (Exception) { Fail(); return; }
            if (!upstream.Send(new("hello") { Code = code, Token = address.Token, Hash = hash })) { Fail(); return; }
        }
        upstream.Tick();
        for (int i = 0; i < 64 && upstream.Receive(out var data); i++)
        {
            if (data.Kind == "ping") continue;
            if (data.Kind == "state") { ReadState(data); continue; }
            if (data.Kind == "packet" && stage == "run" && ValidPacket(data) && data.Target == localId && members.Any(m => m.PlayerId == data.Id))
            { Enqueue(data.Id!, data.PacketKind, data.Bytes!); continue; }
            Fail(); return;
        }
        if (upstream.Closed || members.Length == 0 && Stopwatch.GetElapsedTime(created) > TimeSpan.FromSeconds(5)) Fail();
    }
    private void ReadState(LocalMessage data)
    {
        bool id(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
        if (data.Code != code || data.Hash != hash || data.Scope == Guid.Empty || data.Revision < revision || !id(data.Id) || !id(data.Host) ||
            data.Capacity is < 2 or > 16 || data.Members is not { Length: >= 1 and <= 16 } list || list.Length > data.Capacity ||
            list.Any(m => m is null || !id(m.PlayerId) || m.SessionId == Guid.Empty) || list.Select(m => m.PlayerId).Distinct().Count() != list.Length ||
            list.Select(m => m.SessionId).Distinct().Count() != list.Length || !list.Any(m => m.PlayerId == data.Id) || !list.Any(m => m.PlayerId == data.Host) ||
            data.Prepared is null || data.Prepared.Length > list.Length || data.Prepared.Distinct().Count() != data.Prepared.Length || data.Prepared.Any(p => !list.Any(m => m.PlayerId == p)) ||
            data.Stage is not ("waiting" or "prepare" or "offer" or "run")) { Fail(); return; }
        if (stage != "waiting" && (data.Revision != revision || !members.SequenceEqual(list) || data.Id != localId || data.Host != hostId || data.Scope != activeScope)) { Fail(); return; }
        if (stage == "run" && data.Stage != "run" || data.Stage is "offer" or "run" && (!ownPrepared || data.Prepared.Length != list.Length)) { Fail(); return; }
        localId = data.Id!; hostId = data.Host!; members = list; revision = data.Revision; stage = data.Stage!;
        remoteCapacity = data.Capacity; activeScope = data.Scope; prepared.Clear(); foreach (string p in data.Prepared) prepared.Add(p);
        message = "방 참가 완료";
        if (stage == "offer" && !upstream!.Send(new("ack") { Scope = activeScope, Revision = revision })) Fail();
    }
    private void BeginPreparation()
    {
        if (stage != "waiting") return;
        stage = "prepare"; preparationStarted = Stopwatch.GetTimestamp();
    }
    public void Prepare()
    {
        if (failed || left || members.Length < 2 || ownPrepared) throw new InvalidOperationException("참가자 준비 상태를 확인하세요.");
        ownPrepared = true; preparationStarted = Stopwatch.GetTimestamp();
        if (host) { BeginPreparation(); prepared.Add(localId); AdvanceBarrier(); }
        else if (!upstream!.Send(new("prepare") { Revision = revision })) Fail();
    }
    private void AdvanceBarrier()
    {
        if (prepared.Count == members.Length && stage == "prepare") { stage = "offer"; receipts.Add(localId); }
        if (stage == "offer" && receipts.Count == members.Length) stage = "run";
        Broadcast();
    }
    private void Broadcast()
    {
        foreach (var peer in peers.Where(p => p.Member is not null))
            if (!peer.Wire.Send(new("state") { Code = code, Hash = hash, Id = peer.Member!.PlayerId, Host = hostId, Scope = scope,
                Revision = revision, Capacity = capacity, Stage = stage, Members = members, Prepared = prepared.ToArray() })) peer.Wire.Dispose();
    }
    public PreparedLobbyConnection TakeConnection()
    {
        if (Bootstrap?.State != LobbyBootstrapState.Ready) throw new InvalidOperationException("전원 연결 준비가 필요합니다.");
        handed = true; return new(host ? scope : activeScope, hostId, localId, hash, Array.AsReadOnly(members), new Transport(this));
    }
    private bool ValidPacket(LocalMessage data) => data.Bytes is { Length: > 0 and <= 1024 } && Enum.IsDefined(data.PacketKind) && members.Any(m => m.PlayerId == data.Target);
    private void Enqueue(string sender, RoundPacketKind kind, byte[] bytes)
    { if (packets.Count >= 256) { Fail(); return; } packets.Enqueue(new(sender, kind, bytes)); }
    private bool Route(string sender, string target, RoundPacketKind kind, byte[] bytes)
    {
        if (target == localId) { if (packets.Count >= 256) return false; Enqueue(sender, kind, bytes); return true; }
        var peer = peers.SingleOrDefault(p => p.Member?.PlayerId == target);
        return peer is not null && peer.Wire.Send(new("packet") { Id = sender, Target = target, PacketKind = kind, Bytes = bytes });
    }
    private sealed class Transport(LocalRoomConnection room) : IRoundTransport
    {
        private bool closed;
        public string LocalId => room.localId;
        public bool IsConnected(string id) => !closed && !room.failed && !room.left && room.stage == "run" && room.members.Any(m => m.PlayerId == id);
        public bool TrySend(string peer, RoundPacketKind kind, byte[] bytes)
        {
            if (!IsConnected(peer) || bytes is not { Length: > 0 and <= 1024 } || !Enum.IsDefined(kind)) return false;
            return room.host ? room.Route(room.localId, peer, kind, bytes) : room.upstream!.Send(new("packet") { Target = peer, PacketKind = kind, Bytes = bytes });
        }
        public IReadOnlyList<RoundInboundPacket> Receive(int maxPackets)
        {
            if (maxPackets is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxPackets));
            var result = new List<RoundInboundPacket>();
            while (!closed && result.Count < maxPackets && room.packets.TryDequeue(out var packet)) result.Add(packet);
            return result;
        }
        public void Dispose() { closed = true; room.packets.Clear(); }
    }
    private void Fail() { failed = true; message = "참가자 연결 또는 방 정보가 변경되었습니다. 방에 다시 참가하세요."; CloseSockets(); }
    private void CloseSockets()
    {
        listener?.Stop(); upstream?.Dispose(); joining?.Dispose(); foreach (var peer in peers) peer.Wire.Dispose(); peers.Clear();
        if (host) address.Remove();
    }
    public void Leave() { left = true; message = "방에서 나왔습니다."; CloseSockets(); }
    public void Dispose() { if (disposed) return; disposed = true; CloseSockets(); }
    public void Search() => throw new InvalidOperationException();
    public void Create(int capacity, string? lobbyId = null) => throw new InvalidOperationException();
    public void Join(string id) => throw new InvalidOperationException();
    public void JoinByCode(string code) => throw new InvalidOperationException();
}
