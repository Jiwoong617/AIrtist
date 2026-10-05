using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

// This boundary is called only by OnlineSessionWorker's dedicated thread.
internal interface IOnlineConnection : IDisposable
{
    EosConnectionSnapshot Authentication { get; }
    EosLobbySnapshot? Lobby { get; }
    LobbyBootstrapSnapshot? Bootstrap { get; }
    void Tick();
    void Search();
    void Create(int capacity);
    void Join(string id);
    void Prepare();
    PreparedLobbyConnection TakeConnection();
    void Leave();
}

internal interface IOnlineConnectionFactory : IDisposable
{
    IOnlineConnection Connect(string settingsPath, string displayName, string compatibilityHash);
}

internal sealed class EosOnlineConnectionFactory : IOnlineConnectionFactory
{
    // Kept across room windows and authentication retries. Shutdown is terminal.
    private EosLibrary? library;
    public IOnlineConnection Connect(string settingsPath, string displayName, string compatibilityHash)
    {
        var settings = EosSettings.Load(settingsPath);
        library ??= new EosLibrary();
        var session = new EosDeviceSession(library, settings, displayName);
        try { session.StartLogin(); return new Connection(session, compatibilityHash); }
        catch { session.Dispose(); throw; }
    }
    public void Dispose() => library?.Dispose();

    private sealed class Connection(EosDeviceSession session, string hash) : IOnlineConnection
    {
        private EosLobbyClient? lobby;
        private LobbyConnectionBootstrap? bootstrap;
        public EosConnectionSnapshot Authentication => session.Snapshot;
        public EosLobbySnapshot? Lobby => lobby?.Snapshot;
        public LobbyBootstrapSnapshot? Bootstrap => bootstrap?.Snapshot;
        public void Tick()
        {
            session.Tick();
            if (session.Snapshot.State == EosConnectionState.Connected && lobby is null)
                lobby = session.CreateLobbyClient(hash);
        }
        private EosLobbyClient Room => lobby ?? throw new InvalidOperationException("인증을 기다려 주세요.");
        public void Search() => Room.Search();
        public void Create(int capacity) => Room.Create(capacity);
        public void Join(string id) => Room.Join(id);
        public void Prepare() => bootstrap = session.PrepareLobbyConnection();
        public PreparedLobbyConnection TakeConnection() => bootstrap!.TakeConnection();
        public void Leave() => Room.Leave();
        public void Dispose() => session.Dispose();
    }
}
