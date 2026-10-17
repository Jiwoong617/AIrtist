using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Platform;
using AIrtist.Core;

namespace AIrtist.Eos;

internal sealed class EosSdkLobbyApi : IEosLobbyApi
{
    private readonly LobbyInterface lobby;
    private readonly ProductUserId local;
    // All callbacks share the DeviceSession owner for cleanup after its Platform.Release.
    private readonly object callbackOwner;
    private readonly Dictionary<string, LobbyDetails> results = new(StringComparer.Ordinal);
    private readonly HashSet<string> leaving = new(StringComparer.Ordinal);
    private LobbySearch? search;
    private ulong updateId, memberId, statusId;
    private string? joinedId;
    private bool disposed;

    internal EosSdkLobbyApi(PlatformInterface platform, string localId, object callbackOwner)
    {
        lobby = platform.GetLobbyInterface();
        local = ProductUserId.FromString(localId);
        if (local == null || !local.IsValid()) throw new ArgumentException("인증된 EOS 참가자 ID가 필요합니다.");
        this.callbackOwner = callbackOwner;
    }

    public bool Start(Action<EosLobbySignal> notify)
    {
        var update = new AddNotifyLobbyUpdateReceivedOptions();
        updateId = lobby.AddNotifyLobbyUpdateReceived(ref update, callbackOwner, (ref LobbyUpdateReceivedCallbackInfo info) =>
        { if (!disposed) notify(new(info.LobbyId?.ToString() ?? "", null, EosLobbySignalKind.Updated)); });
        var member = new AddNotifyLobbyMemberUpdateReceivedOptions();
        memberId = lobby.AddNotifyLobbyMemberUpdateReceived(ref member, callbackOwner, (ref LobbyMemberUpdateReceivedCallbackInfo info) =>
        { if (!disposed) notify(new(info.LobbyId?.ToString() ?? "", info.TargetUserId?.ToString(), EosLobbySignalKind.MemberUpdated)); });
        var status = new AddNotifyLobbyMemberStatusReceivedOptions();
        statusId = lobby.AddNotifyLobbyMemberStatusReceived(ref status, callbackOwner, (ref LobbyMemberStatusReceivedCallbackInfo info) =>
        {
            if (disposed) return;
            EosLobbySignalKind? kind = info.CurrentStatus switch
            {
                LobbyMemberStatus.Joined => EosLobbySignalKind.Joined,
                LobbyMemberStatus.Left => EosLobbySignalKind.Left,
                LobbyMemberStatus.Disconnected => EosLobbySignalKind.Disconnected,
                LobbyMemberStatus.Kicked => EosLobbySignalKind.Kicked,
                LobbyMemberStatus.Promoted => EosLobbySignalKind.Promoted,
                LobbyMemberStatus.Closed => EosLobbySignalKind.Closed,
                _ => null
            };
            if (kind.HasValue) notify(new(info.LobbyId?.ToString() ?? "", info.TargetUserId?.ToString(), kind.Value));
        });
        return updateId != 0 && memberId != 0 && statusId != 0;
    }

    public void Search(string bucketId, Action<EosLobbySearchCompletion> complete, string? lobbyId = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ClearSearch();
        var create = new CreateLobbySearchOptions { MaxResults = EosLobbyClient.MaxSearchResults };
        if (lobby.CreateLobbySearch(ref create, out search) != Result.Success || search == null)
        { ReleaseSearch(); complete(new(false, [])); return; }
        var parameter = new LobbySearchSetParameterOptions
        {
            Parameter = new AttributeData { Key = LobbyInterface.SEARCH_BUCKET_ID, Value = new AttributeDataValue { AsUtf8 = bucketId } },
            ComparisonOp = ComparisonOp.Equal
        };
        var exact = new LobbySearchSetLobbyIdOptions { LobbyId = lobbyId };
        var configured = lobbyId is null ? search.SetParameter(ref parameter) : search.SetLobbyId(ref exact);
        if (configured != Result.Success) { ReleaseSearch(); complete(new(false, [])); return; }
        var find = new LobbySearchFindOptions { LocalUserId = local };
        search.Find(ref find, callbackOwner, (ref LobbySearchFindCallbackInfo info) =>
        {
            if (disposed) return;
            // Exact-ID lookup may return NotFound instead of a successful empty result.
            if (lobbyId is not null && info.ResultCode == Result.NotFound)
            { ReleaseSearch(); complete(new(true, [])); return; }
            if (info.ResultCode != Result.Success || search == null) { ReleaseSearch(); complete(new(false, [])); return; }
            var rooms = new List<EosLobbyListing>();
            try
            {
                var countOptions = new LobbySearchGetSearchResultCountOptions();
                uint count = Math.Min(search.GetSearchResultCount(ref countOptions), (uint)EosLobbyClient.MaxSearchResults);
                for (uint i = 0; i < count; i++)
                {
                    var copy = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = i };
                    if (search.CopySearchResultByIndex(ref copy, out var details) != Result.Success || details == null) continue;
                    var room = ReadListing(details);
                    if (room == null || room.BucketId != bucketId || (lobbyId is not null && room.LobbyId != lobbyId) || !results.TryAdd(room.LobbyId, details)) { details.Release(); continue; }
                    rooms.Add(room);
                }
            }
            finally { ReleaseSearch(); }
            complete(new(true, rooms.AsReadOnly()));
        });
    }

    public void Create(string bucketId, int maxMembers, Action<EosLobbyCompletion> complete, string? lobbyId = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var options = new CreateLobbyOptions
        {
            LocalUserId = local, MaxLobbyMembers = (uint)maxMembers, BucketId = bucketId,
            LobbyId = lobbyId ?? RoomCode.CreateLobbyId(),
            PermissionLevel = LobbyPermissionLevel.Publicadvertised, DisableHostMigration = true,
            PresenceEnabled = false, AllowInvites = false, EnableRTCRoom = false, EnableJoinById = false,
            RejoinAfterKickRequiresInvite = true
        };
        lobby.CreateLobby(ref options, callbackOwner, (ref CreateLobbyCallbackInfo info) =>
            CompleteEntry(info.ResultCode, info.LobbyId?.ToString(), complete));
    }

    public void Join(string lobbyId, Action<EosLobbyCompletion> complete)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!results.TryGetValue(lobbyId, out var details)) { complete(new(false, null)); return; }
        var options = new JoinLobbyOptions { LocalUserId = local, LobbyDetailsHandle = details, PresenceEnabled = false };
        lobby.JoinLobby(ref options, callbackOwner, (ref JoinLobbyCallbackInfo info) =>
            CompleteEntry(info.ResultCode, info.LobbyId?.ToString(), complete));
    }

    private void CompleteEntry(Result result, string? id, Action<EosLobbyCompletion> complete)
    {
        bool success = result == Result.Success && !string.IsNullOrEmpty(id);
        if (success) joinedId = id;
        if (disposed) { if (success) Leave(id!, _ => { }); return; }
        complete(new(success, id));
    }

    // Also callable for cleanup after Dispose while its owning Platform is still being ticked.
    public void Leave(string lobbyId, Action<EosLobbyCompletion> complete)
    {
        if (!leaving.Add(lobbyId)) return;
        var options = new LeaveLobbyOptions { LocalUserId = local, LobbyId = lobbyId };
        lobby.LeaveLobby(ref options, callbackOwner, (ref LeaveLobbyCallbackInfo info) =>
        {
            leaving.Remove(lobbyId);
            bool success = info.ResultCode is Result.Success or Result.NotFound;
            if (success && joinedId == lobbyId) joinedId = null;
            complete(new(success, info.LobbyId?.ToString()));
        });
    }

    public EosLobbyMembership? ReadMembership(string lobbyId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var options = new CopyLobbyDetailsHandleOptions { LocalUserId = local, LobbyId = lobbyId };
        if (lobby.CopyLobbyDetailsHandle(ref options, out var details) != Result.Success || details == null) return null;
        try
        {
            var room = ReadListing(details); if (room == null) return null;
            var countOptions = new LobbyDetailsGetMemberCountOptions();
            uint count = details.GetMemberCount(ref countOptions);
            if (count is < 1 or > 16) return null;
            var members = new string[count];
            var memberData = new Dictionary<string, string?>();
            var waitingMembers = new Dictionary<string, string?>();
            for (uint i = 0; i < count; i++)
            {
                var index = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
                var user = details.GetMemberByIndex(ref index);
                if (user == null || !user.IsValid()) return null;
                members[i] = user.ToString();
                var attributeOptions = new LobbyDetailsCopyMemberAttributeByKeyOptions { TargetUserId = user, AttrKey = LobbyConnectionBootstrap.MemberKey };
                var result = details.CopyMemberAttributeByKey(ref attributeOptions, out var attribute);
                if (result is not (Result.Success or Result.NotFound)) return null;
                if (result == Result.Success && attribute?.Data?.Value.AsUtf8 is null) return null;
                memberData.Add(members[i], attribute?.Data?.Value.AsUtf8?.ToString());
                attributeOptions.AttrKey = "ART_WAIT_MEMBER_V1";
                var waitingResult = details.CopyMemberAttributeByKey(ref attributeOptions, out var waitingAttribute);
                if (waitingResult is not (Result.Success or Result.NotFound)) return null;
                waitingMembers.Add(members[i], waitingAttribute?.Data?.Value.AsUtf8?.ToString());
            }
            var proposalOptions = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = LobbyConnectionBootstrap.ProposalKey };
            var proposalResult = details.CopyAttributeByKey(ref proposalOptions, out var proposal);
            if (proposalResult is not (Result.Success or Result.NotFound)) return null;
            if (proposalResult == Result.Success && proposal?.Data?.Value.AsUtf8 is null) return null;
            proposalOptions.AttrKey = "ART_WAIT_ROOM_V1";
            var waitingRoomResult = details.CopyAttributeByKey(ref proposalOptions, out var waitingRoom);
            if (waitingRoomResult is not (Result.Success or Result.NotFound)) return null;
            return new(room, Array.AsReadOnly(members)) { ConnectionProposal = proposal?.Data?.Value.AsUtf8?.ToString(), MemberConnectionData = memberData,
                WaitingSettings = waitingRoom?.Data?.Value.AsUtf8?.ToString(), WaitingMembers = waitingMembers };
        }
        finally { details.Release(); }
    }

    private static EosLobbyListing? ReadListing(LobbyDetails details)
    {
        var options = new LobbyDetailsCopyInfoOptions();
        if (details.CopyInfo(ref options, out var info) != Result.Success || info is not { } value ||
            value.MaxMembers is < 2 or > 16 || value.AvailableSlots > value.MaxMembers || value.LobbyOwnerUserId == null || !value.LobbyOwnerUserId.IsValid() ||
            value.RTCRoomEnabled || value.PresenceEnabled || value.AllowHostMigration || value.PermissionLevel != LobbyPermissionLevel.Publicadvertised) return null;
        return new(value.LobbyId?.ToString() ?? "", value.LobbyOwnerUserId.ToString(), value.BucketId?.ToString() ?? "", (int)value.MaxMembers, (int)value.AvailableSlots);
    }

    public void WriteConnectionAttribute(string lobbyId, bool owner, string value, Action<bool> complete)
        => WriteAttribute(lobbyId, owner, value, owner ? LobbyConnectionBootstrap.ProposalKey : LobbyConnectionBootstrap.MemberKey, complete);

    public void WriteWaitingAttribute(string lobbyId, bool owner, string value, Action<bool> complete)
        => WriteAttribute(lobbyId, owner, value, owner ? "ART_WAIT_ROOM_V1" : "ART_WAIT_MEMBER_V1", complete);

    private void WriteAttribute(string lobbyId, bool owner, string value, string key, Action<bool> complete)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var options = new UpdateLobbyModificationOptions { LobbyId = lobbyId, LocalUserId = local };
        if (lobby.UpdateLobbyModification(ref options, out var modification) != Result.Success || modification is null)
        { complete(false); return; }
        try
        {
            var data = new AttributeData { Key = key,
                Value = new AttributeDataValue { AsUtf8 = value } };
            Result result;
            if (owner)
            {
                var add = new LobbyModificationAddAttributeOptions { Attribute = data, Visibility = LobbyAttributeVisibility.Public };
                result = modification.AddAttribute(ref add);
            }
            else
            {
                var add = new LobbyModificationAddMemberAttributeOptions { Attribute = data, Visibility = LobbyAttributeVisibility.Public };
                result = modification.AddMemberAttribute(ref add);
            }
            if (result != Result.Success) { complete(false); return; }
            var update = new UpdateLobbyOptions { LobbyModificationHandle = modification };
            lobby.UpdateLobby(ref update, callbackOwner, (ref UpdateLobbyCallbackInfo info) =>
            { if (!disposed) complete(info.ResultCode == Result.Success && info.LobbyId?.ToString() == lobbyId); });
        }
        finally { modification.Release(); }
    }

    private void ReleaseSearch() { search?.Release(); search = null; }
    public void ClearSearch()
    {
        foreach (var details in results.Values) details.Release();
        results.Clear();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (updateId != 0) lobby.RemoveNotifyLobbyUpdateReceived(updateId);
        if (memberId != 0) lobby.RemoveNotifyLobbyMemberUpdateReceived(memberId);
        if (statusId != 0) lobby.RemoveNotifyLobbyMemberStatusReceived(statusId);
        ReleaseSearch(); ClearSearch();
        if (joinedId != null) Leave(joinedId, _ => { });
    }
}
