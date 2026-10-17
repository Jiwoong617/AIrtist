using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

internal static class PlayerNickname
{
    internal static string Normalize(string? value)
    {
        if (value is null) throw new ArgumentException("닉네임은 1~16자로 입력해 주세요.");
        value = value.Trim().Normalize();
        if (value.Length is < 1 or > 16 || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new ArgumentException("닉네임은 1~16자로 입력해 주세요.");
        return value;
    }
}
internal sealed record WaitingPlayer(string Id, string Name, bool Ready, bool Host);
internal sealed record WaitingRoom(string Revision, MultiplayerMatchSettings Settings, WaitingPlayer[] Players, bool CanStart, bool Starting, bool Pending);
internal sealed record WaitingIdentity(string N, string? V);
internal sealed record WaitingConfiguration(string V, string R, int N, int W, int G, float? C, int? S, int? Z, bool Start)
{
    internal MultiplayerMatchSettings Settings => MultiplayerMatchSettings.Default with
    {
        RoundCount = N, RoundPolicy = MultiplayerRoundPolicy.Default with
        { WritingDuration = TimeSpan.FromSeconds(W), GenerationDuration = TimeSpan.FromSeconds(G), FixedParameters = C is null ? null : new(C.Value, S!.Value, Z) }
    };
    internal static WaitingConfiguration Create(MultiplayerMatchSettings value, string roster) => new(Guid.NewGuid().ToString("N"), roster,
        value.RoundCount, (int)value.RoundPolicy.WritingDuration.TotalSeconds, (int)value.RoundPolicy.GenerationDuration.TotalSeconds,
        value.RoundPolicy.FixedParameters?.Guidance, value.RoundPolicy.FixedParameters?.Steps, value.RoundPolicy.FixedParameters?.Seed, false);
}

// Uses authenticated lobby attributes before the immutable P2P connection barrier begins.
internal sealed class WaitingRoomCoordinator(IOnlineConnection connection, string nickname, MultiplayerMatchSettings initialSettings)
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 4 };
    private MultiplayerMatchSettings settings = initialSettings;
    private WaitingConfiguration? desiredRoom;
    private WaitingIdentity desiredMember = new(PlayerNickname.Normalize(nickname), null);
    private string context = "";
    private string? pendingValue;
    private bool pendingOwner, callbackDone, callbackSucceeded;
    private long lastWrite, writeStarted;
    private long hostMembershipRevision = -1;
    private long? saveStarted;
    private long? startObserved;
    private bool wrote;
    public WaitingRoom? Snapshot { get; private set; }
    public bool BeginConnection { get; private set; }
    public IReadOnlyDictionary<string, string>? Names { get; private set; }

    public void ChangeSettings(MultiplayerMatchSettings value)
    {
        if (!value.IsValid() || Snapshot is null || Snapshot.Starting || connection.Authentication.PlayerId != connection.Lobby?.Membership?.Lobby.OwnerId)
            throw new InvalidOperationException("방장만 대기 중에 설정을 변경할 수 있습니다.");
        settings = value; desiredRoom = WaitingConfiguration.Create(value, Roster(connection.Lobby!.Membership!));
    }
    public void SetReady(bool ready)
    {
        var view = Snapshot ?? throw new InvalidOperationException("방 정보를 기다려 주세요.");
        if (view.Starting || view.Pending) throw new InvalidOperationException("방 설정을 확인하고 다시 준비해 주세요.");
        desiredMember = desiredMember with { V = ready ? view.Revision : null };
    }
    public void Start()
    {
        if (Snapshot?.CanStart != true || desiredRoom is null || desiredRoom.V != Snapshot.Revision || pendingValue is not null)
            throw new InvalidOperationException("다른 참가자가 모두 준비해야 시작할 수 있습니다.");
        desiredRoom = desiredRoom with { Start = true };
    }
    public void Tick()
    {
        var room = connection.Lobby?.Membership;
        if (room is null || connection.Lobby?.State != EosLobbyState.Joined) { Snapshot = null; return; }
        string local = connection.Authentication.PlayerId!;
        bool host = room.Lobby.OwnerId == local;
        string epoch = room.Lobby.LobbyId + "/" + room.Lobby.OwnerId + "/" + local;
        if (context != epoch)
        {
            context = epoch; pendingValue = null; saveStarted = startObserved = null; hostMembershipRevision = -1; desiredMember = desiredMember with { V = null };
            desiredRoom = host ? WaitingConfiguration.Create(settings, Roster(room)) : null;
        }
        if (pendingValue is not null)
        {
            string? observed = pendingOwner ? room.WaitingSettings : room.WaitingMembers.GetValueOrDefault(local);
            if (callbackDone && (!callbackSucceeded || observed == pendingValue))
            {
                if (callbackSucceeded) saveStarted = null;
                pendingValue = null;
            }
            else if (Stopwatch.GetElapsedTime(writeStarted) > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("방 정보 저장 시간 초과");
        }
        string roster = Roster(room);
        if (host && (desiredRoom?.R != roster || hostMembershipRevision != connection.Lobby!.MembershipRevision))
        {
            if (BeginConnection) throw new InvalidOperationException("시작 중 참가자가 변경되었습니다.");
            desiredRoom = WaitingConfiguration.Create(settings, roster);
            hostMembershipRevision = connection.Lobby!.MembershipRevision;
        }
        var config = Read<WaitingConfiguration>(room.WaitingSettings);
        if (!Valid(config) || config!.R != roster) config = null;
        var identities = room.Members.ToDictionary(id => id, id => Read<WaitingIdentity>(room.WaitingMembers.GetValueOrDefault(id)));
        foreach (var id in identities.Keys.ToArray())
        {
            try { if (identities[id] is not { } item || PlayerNickname.Normalize(item.N) != item.N) identities[id] = null; }
            catch (ArgumentException) { identities[id] = null; }
        }
        bool allKnown = identities.Values.All(v => v is not null);
        bool allReady = config is not null && allKnown && room.Members.Where(id => id != room.Lobby.OwnerId).All(id => identities[id]!.V == config.V);
        bool changes = pendingValue is not null || host && JsonSerializer.Serialize(desiredRoom, Json) != room.WaitingSettings ||
            JsonSerializer.Serialize(desiredMember, Json) != room.WaitingMembers.GetValueOrDefault(local);
        Snapshot = config is null ? null : new(config.V, config.Settings,
            room.Members.Select(id => new WaitingPlayer(id, identities[id]?.N ?? "접속 중", id == room.Lobby.OwnerId || identities[id]?.V == config.V, id == room.Lobby.OwnerId)).ToArray(),
            host && !changes && !config.Start && room.Members.Count >= 2 && allReady, config.Start, changes);
        if (BeginConnection) return;
        if (config?.Start == true)
        {
            // EOS room and member notifications may arrive in different orders.
            // The host saw all readiness before Start; peers wait for the same data.
            startObserved ??= Stopwatch.GetTimestamp();
            if (changes || !allKnown || !allReady || room.Members.Count < 2)
            {
                if (Stopwatch.GetElapsedTime(startObserved.Value) > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException("시작 시 참가자 준비 정보가 일치하지 않습니다.");
                return;
            }
            Names = identities.ToDictionary(p => p.Key, p => p.Value!.N);
            settings = config.Settings; BeginConnection = true; return;
        }
        startObserved = null;
        if (pendingValue is not null || wrote && Stopwatch.GetElapsedTime(lastWrite) < TimeSpan.FromMilliseconds(500)) return;
        string memberValue = JsonSerializer.Serialize(desiredMember, Json);
        if (room.WaitingMembers.GetValueOrDefault(local) != memberValue) { Write(false, memberValue); return; }
        if (host)
        {
            string roomValue = JsonSerializer.Serialize(desiredRoom, Json);
            if (room.WaitingSettings != roomValue) Write(true, roomValue);
        }
    }
    private void Write(bool owner, string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 256) throw new InvalidOperationException("방 정보가 너무 깁니다.");
        pendingOwner = owner; pendingValue = value; callbackDone = false;
        wrote = true; lastWrite = writeStarted = Stopwatch.GetTimestamp();
        saveStarted ??= writeStarted;
        if (Stopwatch.GetElapsedTime(saveStarted.Value) > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("방 정보 저장 시간 초과");
        string epoch = context;
        connection.WriteWaitingAttribute(owner, value, success =>
        { if (context == epoch) { callbackSucceeded = success; callbackDone = true; } });
    }
    private static T? Read<T>(string? value) where T : class
    {
        if (value is null || value.Length > 256) return null;
        try { return JsonSerializer.Deserialize<T>(value, Json); } catch (JsonException) { return null; }
    }
    private static bool Valid(WaitingConfiguration? value)
    {
        if (value is null || !Guid.TryParseExact(value.V, "N", out _) || value.R is not { Length: 64 } || !value.R.All(char.IsAsciiHexDigit) ||
            (value.C is null) != (value.S is null) || (value.C is null) != (value.Z is null)) return false;
        try { return value.Settings.IsValid(); } catch (ArgumentException) { return false; }
    }
    private static string Roster(EosLobbyMembership room) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(room.Lobby.OwnerId + "/" + string.Join("/", room.Members.Order(StringComparer.Ordinal)))));
}
