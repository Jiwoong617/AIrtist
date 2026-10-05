using System.Diagnostics;
using System.IO;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

internal enum OnlinePhase { Offline, Connecting, Connected, Leaving, Failed, Closed }
internal enum OnlineAction { Connect, Search, Create, Join, Prepare, Ready, Settings, Start, Translate, Confirm, Revise, Cancel, Vote, CloseVoting, NextRound }
internal sealed record OnlineCommand(OnlineAction Action, long Generation, string Text = "", string Extra = "",
    int Number = 0, long Revision = 0, Guid RoundId = default, bool Ready = false, MultiplayerMatchSettings? Settings = null);
internal sealed record OnlineSnapshot(long Generation, OnlinePhase Phase, bool HasSession, string? LocalId, EosLobbySnapshot? Room,
    LobbyBootstrapSnapshot? Connection, LobbySessionSnapshot? Lobby, MatchRuntimeSnapshot? Match, string Message, string? Error);

// EOS, transport, Core Tick, commands and disposal all run on one OS thread.
// UI reads published snapshots and queues bounded, session/round/revision-tagged commands.
internal sealed class OnlineSessionWorker
{
    private readonly object gate = new();
    private readonly Queue<OnlineCommand> commands = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IOnlineConnectionFactory factory;
    private readonly MultiplayerCompatibility compatibility;
    private readonly Word[] words;
    private readonly IDrawingAi ai;
    private readonly Thread thread;
    private IOnlineConnection? connection;
    private MultiplayerLobbySession? core;
    private TaskCompletionSource? disconnect;
    private bool disconnectRequested, stopping, leaving, failed;
    private long generation, leaveStarted;
    private string message = "접속 후 방을 만들거나 검색해 주세요.", error = "";
    private OnlineSnapshot snapshot = new(0, OnlinePhase.Offline, false, null, null, null, null, null, "접속 후 방을 만들거나 검색해 주세요.", null);
    public OnlineSnapshot Snapshot => Volatile.Read(ref snapshot);

    public OnlineSessionWorker(MultiplayerCompatibility compatibility, Word[] words, IDrawingAi ai, IOnlineConnectionFactory? factory = null)
    {
        this.compatibility = compatibility; this.words = words; this.ai = ai;
        this.factory = factory ?? new EosOnlineConnectionFactory();
        thread = new Thread(Run) { Name = "AIrtist online", IsBackground = true };
        thread.Start();
    }

    public bool Post(OnlineCommand command)
    {
        lock (gate)
        {
            if (stopping || disconnect is not null || finished.Task.IsCompleted || commands.Count >= 32 ||
                command.Text.Length > 4096 || command.Extra.Length > 320) return false;
            commands.Enqueue(command); wake.Set(); return true;
        }
    }

    public Task DisconnectAsync()
    {
        lock (gate)
        {
            if (finished.Task.IsCompleted) return finished.Task;
            if (disconnect is not null) return disconnect.Task;
            disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disconnectRequested = true; commands.Clear(); wake.Set(); return disconnect.Task;
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (finished.Task.IsCompleted) return finished.Task;
            stopping = true; disconnectRequested = true; commands.Clear(); wake.Set(); return finished.Task;
        }
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                bool close; OnlineCommand[] pending;
                lock (gate)
                {
                    close = disconnectRequested; disconnectRequested = false;
                    pending = commands.ToArray(); commands.Clear();
                }
                if (close && !leaving) BeginDisconnect();
                foreach (var command in pending)
                {
                    lock (gate) { if (disconnectRequested || stopping) break; }
                    if (leaving || command.Generation != generation) continue;
                    try { Execute(command); error = ""; }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
                    { error = command.Action >= OnlineAction.Ready ? ex.Message : "현재 상태에서는 요청을 처리할 수 없습니다. 상태를 확인하고 다시 시도해 주세요."; }
                    catch (Exception) { Fail("접속 처리에 실패했습니다. 설정 파일과 앱 구성을 확인해 주세요."); }
                }
                try { Tick(); }
                catch (Exception) { Fail("온라인 처리가 중단되었습니다. 나간 뒤 다시 접속해 주세요."); if (leaving) EndDisconnect(false); }
                Publish();
                lock (gate) { if (stopping && connection is null && !leaving) break; }
                wake.WaitOne(20);
            }
        }
        catch (Exception) { message = "온라인 작업 스레드가 중단되었습니다. 앱을 다시 실행해 주세요."; }
        finally
        {
            // Never pass arbitrary SDK/parser exception text (or credentials) to the UI/log.
            try { core?.Dispose(); } catch { }
            try { connection?.Dispose(); } catch { }
            try { factory.Dispose(); } catch { message = "온라인 종료 중 오류가 발생했습니다. 앱을 다시 실행해 주세요."; }
            lock (gate)
            {
                stopping = true; commands.Clear();
                Volatile.Write(ref snapshot, Snapshot with { Phase = OnlinePhase.Closed, Message = message });
                disconnect?.TrySetResult(); disconnect = null;
                finished.TrySetResult(); wake.Dispose();
            }
        }
    }

    private void Execute(OnlineCommand command)
    {
        if (command.Action == OnlineAction.Connect)
        {
            if (connection is not null) throw new InvalidOperationException();
            generation++; failed = false; message = "기기 인증 중입니다.";
            try { connection = factory.Connect(command.Text, command.Extra, compatibility.ConnectionHash()); }
            catch (InvalidDataException ex) { Fail(ex.Message); } // EosSettings deliberately sanitizes these.
            return;
        }
        if (connection is null || failed) throw new InvalidOperationException();
        var match = core?.Match;
        switch (command.Action)
        {
            case OnlineAction.Search: connection.Search(); break;
            case OnlineAction.Create: connection.Create(command.Number); break;
            case OnlineAction.Join: connection.Join(command.Text); break;
            case OnlineAction.Prepare:
                if (connection.Bootstrap is not null) throw new InvalidOperationException();
                connection.Prepare(); break;
            case OnlineAction.Ready: RequiredLobby().SetReady(command.Revision, command.Ready); break;
            case OnlineAction.Settings: RequiredLobby().UpdateSettings(command.Revision, command.Settings!); break;
            case OnlineAction.Start: RequiredLobby().Start(command.Revision); break;
            case OnlineAction.Translate: RequiredMatch().PrepareTranslation(command.RoundId, command.Text); break;
            case OnlineAction.Confirm: RequiredMatch().Confirm(command.RoundId, command.Revision, command.Text, command.Number); break;
            case OnlineAction.Revise: RequiredMatch().Revise(command.RoundId, command.Revision); break;
            case OnlineAction.Cancel: RequiredMatch().CancelAi(command.RoundId); break;
            case OnlineAction.Vote: RequiredMatch().Vote(command.RoundId, command.Text); break;
            case OnlineAction.CloseVoting: RequiredMatch().CloseVoting(command.RoundId); break;
            case OnlineAction.NextRound: RequiredMatch().AdvanceRound(command.RoundId); break;
            default: throw new ArgumentException();
        }
        MultiplayerLobbySession RequiredLobby() => core ?? throw new InvalidOperationException();
        MultiplayerMatchRuntime RequiredMatch() => match ?? throw new InvalidOperationException();
    }

    private void Tick()
    {
        if (connection is null) return;
        if (!failed || leaving) connection.Tick();
        if (leaving)
        {
            if (connection.Lobby?.State == EosLobbyState.Idle) EndDisconnect(true);
            else if (connection.Lobby?.State == EosLobbyState.Failed || Stopwatch.GetElapsedTime(leaveStarted) >= TimeSpan.FromSeconds(5)) EndDisconnect(false);
            return;
        }
        if (connection.Authentication.State == EosConnectionState.Failed || connection.Lobby?.State == EosLobbyState.Failed ||
            connection.Bootstrap?.State == LobbyBootstrapState.Failed)
            Fail("인증·방 또는 참가자 연결이 변경되어 중단되었습니다. 나간 뒤 다시 접속해 주세요.");
        if (!failed && core is null && connection.Bootstrap?.State == LobbyBootstrapState.Ready)
        {
            var prepared = connection.TakeConnection();
            try { core = MultiplayerLobbySession.FromPreparedConnection(prepared, compatibility, words, ai); }
            catch { prepared.Transport.Dispose(); throw; }
        }
        if (!failed) core?.Tick();
    }

    private void Fail(string reason)
    {
        failed = true; message = reason; core?.Abort();
    }
    private void BeginDisconnect()
    {
        generation++; error = ""; leaving = true; leaveStarted = Stopwatch.GetTimestamp();
        core?.Dispose(); core = null; message = "방에서 나가는 중입니다…";
        try
        {
            if (connection?.Lobby?.State == EosLobbyState.Joined) connection.Leave();
            else EndDisconnect(connection?.Lobby?.Membership is null);
        }
        catch { EndDisconnect(false); }
    }
    private void EndDisconnect(bool confirmed)
    {
        connection?.Dispose(); connection = null; leaving = failed = false;
        message = confirmed ? "접속을 종료했습니다." : "로컬 접속을 종료했습니다. 서비스의 방 나가기 응답은 확인하지 못했습니다.";
        Publish();
        lock (gate) { disconnect?.TrySetResult(); disconnect = null; }
    }
    private void Publish()
    {
        var auth = connection?.Authentication;
        var phase = leaving ? OnlinePhase.Leaving : failed ? OnlinePhase.Failed : connection is null ? OnlinePhase.Offline :
            auth?.State is EosConnectionState.Connected or EosConnectionState.Renewing ? OnlinePhase.Connected : OnlinePhase.Connecting;
        string status = leaving || failed || connection is null ? message : core?.Snapshot().Message ?? connection.Bootstrap?.Message ?? connection.Lobby?.Message ?? auth!.Message;
        Volatile.Write(ref snapshot, new(generation, phase, connection is not null, auth?.PlayerId, connection?.Lobby, connection?.Bootstrap,
            core?.Snapshot(), core?.Match?.Snapshot(), status, string.IsNullOrEmpty(error) ? null : error));
    }
}
