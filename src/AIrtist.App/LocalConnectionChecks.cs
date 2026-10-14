using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

internal static class LocalConnectionChecks
{
    private static readonly string Hash = new('a', 64);
    private static void Check(bool value, string text) => OnlineSmoke.Require(value, text);
    private static async Task Pump(Func<bool> condition, params IOnlineConnection[] connections)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            foreach (var connection in connections) connection.Tick();
            if (timer.Elapsed.TotalSeconds > 8) throw new TimeoutException("Local boundary check");
            await Task.Delay(10);
        }
    }
    internal static async Task Run(RuntimeAssets assets)
    {
        // Cancellation while another app owns the GPU gate must not load a model or
        // retain ownership. This check runs before any diagnostic participant generates.
        using (var gate = new Mutex(false, @"Local\AIrtist.Generation.v1"))
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("GPU gate diagnostic setup");
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                bool canceled = false;
                try { await Task.Run(() => new LocalDrawingAi(assets).Generate("a blue circle", 1, cancel.Token, _ => { })).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Queued GPU generation ignored cancellation");
            }
            finally { gate.ReleaseMutex(); }
        }
        using (var host = LocalRoomConnection.Host(Hash, 2, () => true))
        {
            var address = LocalRoomAddress.Find(host.Code)!;
            bool incompatible = false;
            try { using var bad = new LocalRoomConnection(new('b', 64), address); }
            catch (ArgumentException) { incompatible = true; }
            Check(incompatible, "Local incompatible model accepted");
            using (var malformed = new TcpClient(AddressFamily.InterNetwork))
            {
                await malformed.ConnectAsync(IPAddress.Loopback, address.Port);
                var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, LocalWire.MaxFrame + 1);
                await malformed.GetStream().WriteAsync(header);
                var read = malformed.GetStream().ReadAsync(new byte[1]).AsTask();
                await Pump(() => read.IsCompleted, host);
                try { Check(await read == 0, "Oversized frame accepted"); } catch (System.IO.IOException) { }
            }
            using var guest = new LocalRoomConnection(Hash, address);
            await Pump(() => host.ParticipantCount == 2 && guest.ParticipantCount == 2, host, guest);
            using var extra = new LocalRoomConnection(Hash, address);
            await Pump(() => extra.Lobby.State == EosLobbyState.Failed, host, guest, extra);
            Check(host.ParticipantCount == 2, "Local capacity exceeded");
            host.Prepare();
            await Pump(() => guest.Lobby.State == EosLobbyState.Joined, host, guest);
            Check(host.Bootstrap!.State != LobbyBootstrapState.Ready, "Partial preparation opened lobby");
            guest.Prepare();
            await Pump(() => host.Bootstrap?.State == LobbyBootstrapState.Ready && guest.Bootstrap?.State == LobbyBootstrapState.Ready, host, guest);
            var hostReady = host.TakeConnection(); var guestReady = guest.TakeConnection();
            using var ht = hostReady.Transport; using var gt = guestReady.Transport;
            Check(hostReady.ScopeId == guestReady.ScopeId && hostReady.Members.SequenceEqual(guestReady.Members), "Local roster/scope mismatch");
            Check(!gt.TrySend(hostReady.LocalId, RoundPacketKind.ControlToHost, new byte[1025]), "Oversized packet accepted");
            Check(!gt.TrySend("unknown", RoundPacketKind.ControlToHost, [1]), "Unknown recipient accepted");
            Check(gt.TrySend(hostReady.LocalId, RoundPacketKind.ControlToHost, [1, 2, 3]), "Local packet send failed");
            IReadOnlyList<RoundInboundPacket> received = [];
            await Pump(() => (received = ht.Receive(8)).Count > 0, host, guest);
            Check(received.Single().AuthenticatedSender == guestReady.LocalId && received.Single().Bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "Sender pinning failed");
            guest.Leave();
            await Pump(() => host.Lobby.State == EosLobbyState.Failed, host);
            Check(!ht.IsConnected(guestReady.LocalId) && LocalRoomAddress.Find(host.Code) is null, "Departure left active room");
        }
        await SpoofedSender();
        using var factory = new RemoteFactory();
        using var auto = new AutomaticRoomConnection(factory, "", "검사", Hash);
        auto.Create(3); auto.Tick();
        string code = RoomCode.FromLobbyId(auto.Lobby!.Membership!.Lobby.LobbyId)!;
        Check(factory.Remote.CreatedId == RoomCode.ToLobbyId(code), "Remote and local codes differ");
        factory.Remote.HasGuest = true; auto.Tick();
        Check(auto.Authentication.PlayerId == RemoteConnection.Owner && LocalRoomAddress.Find(code) is null, "Online-first room did not select EOS");
        auto.Prepare(); Check(factory.Remote.Prepared, "Online preparation was not delegated");
    }
    private static async Task SpoofedSender()
    {
        using var host = LocalRoomConnection.Host(Hash, 2, () => true);
        var address = LocalRoomAddress.Find(host.Code)!;
        var socket = new TcpClient(AddressFamily.InterNetwork); await socket.ConnectAsync(IPAddress.Loopback, address.Port);
        using var peer = new LocalWire(socket);
        peer.Send(new("hello") { Code = address.Code, Token = address.Token, Hash = Hash });
        LocalMessage? state = null;
        bool State(string stage)
        {
            while (peer.Receive(out var data)) if (data.Kind == "state") state = data;
            return state?.Stage == stage;
        }
        await Pump(() => State("waiting"), host);
        host.Prepare(); peer.Send(new("prepare") { Revision = state!.Revision });
        await Pump(() => State("offer"), host);
        peer.Send(new("ack") { Revision = state!.Revision, Scope = state.Scope });
        await Pump(() => State("run"), host);
        peer.Send(new("packet") { Id = state!.Host, Target = state.Host, PacketKind = RoundPacketKind.ControlToHost, Bytes = [1] });
        await Pump(() => host.Lobby.State == EosLobbyState.Failed, host);
    }
    private sealed class RemoteFactory : IOnlineConnectionFactory
    {
        internal readonly RemoteConnection Remote = new();
        public IOnlineConnection Connect(string path, string name, string hash) => Remote;
        public void Dispose() { }
    }
    private sealed class RemoteConnection : IOnlineConnection
    {
        internal const string Owner = "11111111111111111111111111111111";
        internal bool HasGuest, Prepared;
        internal string? CreatedId;
        public EosConnectionSnapshot Authentication => new(EosConnectionState.Connected, Owner, "");
        public EosLobbySnapshot Lobby => new(CreatedId is null ? EosLobbyState.Idle : EosLobbyState.Joined, [], CreatedId is null ? null :
            new(new(CreatedId, Owner, Hash, 3, HasGuest ? 1 : 2), HasGuest ? [Owner, new('2', 32)] : [Owner]), 1, "");
        public LobbyBootstrapSnapshot? Bootstrap => null;
        public void Create(int capacity, string? lobbyId = null) => CreatedId = lobbyId;
        public void Prepare() => Prepared = true;
        public void Tick() { }
        public void Dispose() { }
        public void Leave() { }
        public void Search() => throw new NotSupportedException();
        public void Join(string id) => throw new NotSupportedException();
        public void JoinByCode(string code) => throw new NotSupportedException();
        public PreparedLobbyConnection TakeConnection() => throw new NotSupportedException();
    }
}
