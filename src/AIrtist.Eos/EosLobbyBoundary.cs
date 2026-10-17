namespace AIrtist.Eos;

public enum EosLobbyState { Idle, Searching, Creating, Joining, Joined, Leaving, Failed, Disposed }
public sealed record EosLobbyListing(string LobbyId, string OwnerId, string BucketId, int MaxMembers, int AvailableSlots);
// Only populated from EOS CopyLobbyDetailsHandle/GetMemberByIndex, never from a peer packet.
public sealed record EosLobbyMembership(EosLobbyListing Lobby, IReadOnlyList<string> Members)
{
    public string? ConnectionProposal { get; init; }
    public IReadOnlyDictionary<string, string?> MemberConnectionData { get; init; } = new Dictionary<string, string?>();
    public string? WaitingSettings { get; init; }
    public IReadOnlyDictionary<string, string?> WaitingMembers { get; init; } = new Dictionary<string, string?>();
}
public sealed record EosLobbySnapshot(EosLobbyState State, IReadOnlyList<EosLobbyListing> SearchResults,
    EosLobbyMembership? Membership, long MembershipRevision, string Message);

internal enum EosLobbySignalKind { Updated, MemberUpdated, Joined, Left, Disconnected, Kicked, Promoted, Closed }
internal sealed record EosLobbySignal(string LobbyId, string? PlayerId, EosLobbySignalKind Kind);
internal readonly record struct EosLobbyCompletion(bool Success, string? LobbyId);
internal sealed record EosLobbySearchCompletion(bool Success, IReadOnlyList<EosLobbyListing> Results);

// One authenticated local player, one active room and one operation at a time.
// Dispose unsubscribes/releases handles and attempts to leave known membership. A late successful
// create/join must also be left by the SDK adapter after disposal, while the platform is still alive.
internal interface IEosLobbyApi : IDisposable
{
    bool Start(Action<EosLobbySignal> notify);
    void Search(string bucketId, Action<EosLobbySearchCompletion> complete, string? lobbyId = null);
    void Create(string bucketId, int maxMembers, Action<EosLobbyCompletion> complete, string? lobbyId = null);
    void Join(string lobbyId, Action<EosLobbyCompletion> complete);
    void Leave(string lobbyId, Action<EosLobbyCompletion> complete);
    EosLobbyMembership? ReadMembership(string lobbyId);
    void WriteConnectionAttribute(string lobbyId, bool owner, string value, Action<bool> complete);
    void WriteWaitingAttribute(string lobbyId, bool owner, string value, Action<bool> complete) => complete(false);
    void ClearSearch();
}
