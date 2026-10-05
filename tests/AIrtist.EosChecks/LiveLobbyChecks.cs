using AIrtist.Eos;
using System.Diagnostics;
using System.Security.Cryptography;

// Explicit opt-in integration check. Creates one disposable two-slot service lobby, never a match.
// A fresh compatibility bucket keeps this run separate from game rooms and other checks.
internal static class LiveLobbyChecks
{
    public static void Run(EosDeviceSession session, CancellationToken cancellation)
    {
        string compatibility = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        using var lobby = session.CreateLobbyClient(compatibility);
        try
        {
            lobby.Search();
            Wait(session, lobby, EosLobbyState.Idle, cancellation);
            if (lobby.Snapshot.SearchResults.Count != 0)
                throw new InvalidOperationException("독립된 검사 범위에 예상하지 않은 방이 있습니다.");
            Console.WriteLine("PASS: 실제 서비스 방 검색 완료(독립 검사 범위 0개).");

            lobby.Create(2);
            Wait(session, lobby, EosLobbyState.Joined, cancellation);
            var membership = lobby.Snapshot.Membership;
            if (membership is null || membership.Lobby.OwnerId != session.Snapshot.PlayerId ||
                membership.Lobby.MaxMembers != 2 || membership.Lobby.AvailableSlots != 1 ||
                membership.Members.Count != 1 || membership.Members[0] != session.Snapshot.PlayerId)
                throw new InvalidOperationException("서비스가 반환한 검사 방의 방장·정원·참가 명단이 다릅니다.");
            string createdId = membership.Lobby.LobbyId;
            Console.WriteLine("PASS: 실제 서비스 방 생성·명단 조회 완료(정원 2, 현재 1명).");

            lobby.Leave();
            Wait(session, lobby, EosLobbyState.Idle, cancellation);
            if (lobby.Snapshot.Membership is not null)
                throw new InvalidOperationException("나간 방의 참가 상태가 남았습니다.");
            Console.WriteLine("PASS: 실제 서비스 방 나가기 완료.");

            lobby.Search();
            Wait(session, lobby, EosLobbyState.Idle, cancellation);
            if (lobby.Snapshot.SearchResults.Any(room => room.LobbyId == createdId))
                throw new InvalidOperationException("나간 검사 방이 검색 결과에 남았습니다.");
            Console.WriteLine("PASS: 나간 검사 방이 검색되지 않음. 다른 PC 참가·P2P·그림 전송은 별도 검사입니다.");
        }
        finally
        {
            // Do not skip a known joined room's leave callback even after Ctrl+C/assertion failure.
            if (lobby.Snapshot.State == EosLobbyState.Joined)
            {
                lobby.Leave();
                Wait(session, lobby, EosLobbyState.Idle, CancellationToken.None);
            }
            else if (lobby.Snapshot.State == EosLobbyState.Leaving)
                Wait(session, lobby, EosLobbyState.Idle, CancellationToken.None);
        }
    }

    private static void Wait(EosDeviceSession session, EosLobbyClient lobby,
        EosLobbyState target, CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(35))
        {
            // Drain an in-flight native create/leave before honoring cancellation so cleanup
            // can see the final membership and explicitly wait for a leave acknowledgement.
            session.Tick();
            if (session.Snapshot.State == EosConnectionState.Failed)
                throw new InvalidOperationException(session.Snapshot.Message);
            if (lobby.Snapshot.State == EosLobbyState.Failed)
                throw new InvalidOperationException(lobby.Snapshot.Message);
            if (lobby.Snapshot.State == target) { cancellation.ThrowIfCancellationRequested(); return; }
            Thread.Sleep(20);
        }
        throw new InvalidOperationException("실제 서비스 로비 검사 시간 초과");
    }
}
