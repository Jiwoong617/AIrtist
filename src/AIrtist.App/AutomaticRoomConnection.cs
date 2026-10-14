using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

// No mode picker: same-user loopback registration wins code lookup; other codes use EOS.
internal sealed class AutomaticRoomFactory(IOnlineConnectionFactory? remoteFactory = null) : IOnlineConnectionFactory
{
    private readonly IOnlineConnectionFactory remote = remoteFactory ?? new EosOnlineConnectionFactory();
    public IOnlineConnection Connect(string path, string name, string hash) => new AutomaticRoomConnection(remote, path, name, hash);
    public void Dispose() => remote.Dispose();
}

internal sealed class AutomaticRoomConnection(IOnlineConnectionFactory factory, string path, string name, string hash) : IOnlineConnection
{
    private enum Mode { Idle, Hosting, Local, Remote }
    private Mode mode;
    private LocalRoomConnection? local;
    private IOnlineConnection? remote;
    private string? pendingCode;
    private int pendingCapacity;
    private bool pendingSearch, remoteUnavailable, remoteLeaving, disposed;
    private readonly string idleId = Guid.NewGuid().ToString("N");
    public EosConnectionSnapshot Authentication => mode == Mode.Remote && remote is not null ? remote.Authentication :
        local?.Authentication ?? new(EosConnectionState.Connected, idleId, "연결 준비 완료");
    public EosLobbySnapshot? Lobby
    {
        get
        {
            if (mode == Mode.Remote) return remote?.Lobby;
            if (local is null) return new(EosLobbyState.Idle, [], null, 0, "");
            var state = local.Lobby;
            if (mode != Mode.Hosting || state.State != EosLobbyState.Joined) return state;
            return state with { Message = remoteUnavailable ? "온라인 접속 실패. 같은 PC에서 방 코드로 참가할 수 있습니다." :
                remote?.Lobby?.State == EosLobbyState.Joined ? "방 참가 완료" : "온라인 방 등록 중…" };
        }
    }
    public LobbyBootstrapSnapshot? Bootstrap => mode == Mode.Remote ? remote?.Bootstrap : local?.Bootstrap;
    public void Create(int capacity, string? lobbyId = null)
    {
        RequireIdle();
        local = LocalRoomConnection.Host(hash, capacity, ClaimLocal); mode = Mode.Hosting;
        pendingCapacity = capacity; StartRemote();
    }
    private bool ClaimLocal()
    {
        if (mode is not (Mode.Hosting or Mode.Local) || remote?.Lobby?.Membership?.Members.Count > 1) return false;
        mode = Mode.Local; return true;
    }
    public void JoinByCode(string code)
    {
        RequireIdle(); code = RoomCode.Normalize(code);
        if (LocalRoomAddress.Find(code) is { } address)
        {
            if (address.Hash != hash) throw new ArgumentException("게임 버전이 일치하지 않습니다.");
            remote?.Dispose(); remote = null;
            local = new(hash, address); mode = Mode.Local; return;
        }
        mode = Mode.Remote; pendingCode = code; StartRemote();
    }
    private void StartRemote()
    {
        if (remote is not null || remoteUnavailable) return;
        try { remote = factory.Connect(path, name, hash); }
        catch (Exception) when (mode == Mode.Hosting) { remoteUnavailable = true; }
    }
    public void Tick()
    {
        if (disposed) return;
        if (remote is not null)
        {
            try
            {
                remote.Tick();
                if (mode == Mode.Local) DrainRemote();
                else if (mode == Mode.Hosting && (remote.Authentication.State == EosConnectionState.Failed || remote.Lobby?.State == EosLobbyState.Failed))
                { remote.Dispose(); remote = null; remoteUnavailable = true; }
                else if (remote.Lobby?.State == EosLobbyState.Idle)
                {
                    if (mode == Mode.Hosting && pendingCapacity > 0)
                    { remote.Create(pendingCapacity, RoomCode.ToLobbyId(local!.Code)); pendingCapacity = 0; }
                    else if (pendingCode is not null) { string code = pendingCode; pendingCode = null; remote.JoinByCode(code); }
                    else if (pendingSearch) { pendingSearch = false; remote.Search(); }
                }
                // Select one roster before any game state is created. Mixed local/remote rooms
                // are intentionally not merged: EOS cannot authenticate multiple Device IDs here.
                if (mode == Mode.Hosting && remote?.Lobby?.Membership?.Members.Count > 1)
                { mode = Mode.Remote; local!.Dispose(); local = null; }
            }
            catch (Exception) when (mode is Mode.Hosting or Mode.Local)
            { remote?.Dispose(); remote = null; remoteUnavailable = true; }
        }
        local?.Tick();
    }
    private void DrainRemote()
    {
        pendingCapacity = 0;
        if (remote!.Lobby?.State == EosLobbyState.Joined && !remoteLeaving) { remote.Leave(); remoteLeaving = true; }
        else if (remote.Authentication.State == EosConnectionState.Failed || remote.Lobby?.State is EosLobbyState.Idle or EosLobbyState.Failed)
        { remote.Dispose(); remote = null; }
    }
    public void Prepare()
    {
        if (mode == Mode.Remote) remote!.Prepare(); else local!.Prepare();
    }
    public PreparedLobbyConnection TakeConnection() => mode == Mode.Remote ? remote!.TakeConnection() : local!.TakeConnection();
    public void Leave()
    {
        if (mode == Mode.Remote) remote!.Leave();
        else
        {
            local?.Leave();
            // If an online mirror exists, let the worker wait for its leave acknowledgement.
            if (remote?.Lobby?.State == EosLobbyState.Joined) { mode = Mode.Remote; remote.Leave(); }
            else { remote?.Dispose(); remote = null; }
        }
    }
    public void Search() { RequireIdle(); mode = Mode.Remote; pendingSearch = true; StartRemote(); }
    public void Join(string id) { if (mode != Mode.Remote) throw new InvalidOperationException(); remote!.Join(id); }
    private void RequireIdle()
    {
        if (disposed || local is not null || remote is not null && remote.Lobby?.State != EosLobbyState.Idle)
            throw new InvalidOperationException("현재 방에서 나간 뒤 다시 시도하세요.");
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; local?.Dispose(); remote?.Dispose();
    }
}
