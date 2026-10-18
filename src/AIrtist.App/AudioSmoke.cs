using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIrtist.Core;

namespace AIrtist.App;

internal static class AudioSmoke
{
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Advance(AudioMixer mixer, double seconds) => mixer.Render(new short[(int)(AudioBank.Rate * seconds)]);
    internal static async Task<object> Run(string output)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "audio");
        var bank = AudioBank.Load(directory); Require(bank.Count == 9, "Nine audio clips required");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        long bytes = 0;
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            byte[] file = File.ReadAllBytes(Path.Combine(directory, entry.GetProperty("path").GetString()!)); bytes += file.Length;
            Require(Convert.ToHexString(SHA256.HashData(file)).Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Audio hash mismatch");
        }
        Require(bytes < 10_000_000, "Audio budget exceeded");
        var loopChecks = new List<object>();
        foreach (var pair in new[] { (MusicTrack.Menu, "bgm_menu"), (MusicTrack.Game, "bgm_game") })
        {
            var mixer = new AudioMixer(); mixer.Load(bank); mixer.SetVolumes(100, 0); mixer.Music(pair.Item1);
            var samples = bank[pair.Item2].Samples;
            // Three full wraps through the actual mixer, comparing every output sample.
            var block = new short[1024]; long checkedFrames = 0; float maxDelta = 0; short previous = 0;
            while (checkedFrames < samples.Length * 3L)
            {
                mixer.Render(block);
                for (int i = 0; i < block.Length; i++)
                {
                    long position = checkedFrames + i;
                    if (position >= AudioMixer.FadeFrames)
                    {
                        short expected = (short)Math.Round(.85f * MathF.Tanh(samples[position % samples.Length] / .85f) * short.MaxValue);
                        Require(block[i] == expected, "Loop inserted silence, repeated or skipped a sample");
                    }
                    maxDelta = Math.Max(maxDelta, Math.Abs(block[i] - previous) / 32768f); previous = block[i];
                }
                checkedFrames += block.Length;
            }
            Require(Math.Abs(samples[0] - samples[^1]) < .015f && maxDelta < .2f, "Loop discontinuity");
            var before = mixer.MusicState; mixer.Music(pair.Item1);
            Require(mixer.MusicState == before && mixer.MusicChanges == 1, "Same track restarted");
            loopChecks.Add(new { track = pair.Item2, loops = 3, frames = checkedFrames, maxDelta });
        }
        using var audio = new GameAudio(false); var m = audio.Mixer;
        audio.Volume(100, 100); audio.Music(MusicTrack.Menu); Advance(m, 1);
        audio.Music(MusicTrack.Game); Advance(m, .4);
        Require(Math.Abs(m.MusicState.MenuGain - .5) < .001 && Math.Abs(m.MusicState.GameGain - .5) < .001, "Crossfade midpoint");
        Advance(m, .4); Require(m.MusicState.MenuGain == 0 && m.MusicState.GameGain == 1, "Crossfade endpoint");
        audio.Cue(SoundCue.UiClick); audio.Music(MusicTrack.Menu); Advance(m, .2);
        Require(m.Count(SoundCue.UiClick) == 1, "Navigation cancelled the button click");
        audio.Cue(SoundCue.GenerationComplete); Advance(m, .1); audio.Cue(SoundCue.GalleryOpen); Advance(m, .8);
        Require(m.Count(SoundCue.GenerationComplete) == 0 && m.Count(SoundCue.GalleryOpen) == 1, "Gallery priority");
        audio.Cue(SoundCue.DeadlineTick); audio.Cue(SoundCue.RoundResult); Advance(m, .2);
        Require(m.Count(SoundCue.DeadlineTick) == 0 && m.Count(SoundCue.RoundResult) == 1, "Result priority");
        Advance(m, 2); audio.Volume(0, 0); var silent = new short[4096]; m.Render(silent);
        Require(silent.All(v => v == 0), "Zero volume is not silent");
        audio.Cue(SoundCue.UiClick); m.Render(silent); Require(silent.All(v => v == 0), "Muted cue leaked");
        audio.Volume(0, 100); audio.Cue(SoundCue.UiClick); m.Render(silent); Require(silent.Any(v => v != 0), "SFX follows BGM mute");
        Advance(m, 1); audio.Volume(100, 0); audio.Cue(SoundCue.ErrorNotice); m.Render(silent);
        Require(silent.Any(v => v != 0) && m.Count(SoundCue.ErrorNotice) == 0, "Independent SFX mute");
        audio.Volume(0, 100); int peak = 0;
        for (int i = 0; i < 200; i++)
        {
            foreach (var cue in Enum.GetValues<SoundCue>()) audio.Cue(cue);
            var chunk = new short[220]; m.Render(chunk); peak = Math.Max(peak, chunk.Max(v => Math.Abs((int)v)));
        }
        Require(peak < 28000 && m.Count(SoundCue.UiClick) < 40, "Overlap/click limiter");
        var transitions = CheckTransitions();
        await MenuWindow.AudioControlsSmoke(output);
        var devices = new List<object>();
        for (int i = 0; i < 3; i++)
        {
            var live = new GameAudio(); live.Volume(15, 30); live.Music(MusicTrack.Menu);
            await OnlineSmoke.Until(() => live.Output!.Opened || live.Output.Finished);
            Require(live.Output!.Opened, "Audio device did not open: " + live.Output.Failure);
            live.Cue(SoundCue.UiClick); await Task.Delay(500);
            Require(live.Output.SubmittedBuffers >= 15 && live.Output.Failure is null, "Audio device stalled");
            live.Dispose(); Require(live.Output.Finished && live.Output.Failure is null, "Audio output did not stop");
            devices.Add(new { iteration = i, buffers = live.Output.SubmittedBuffers, closed = live.Output.Finished });
        }
        Require(AudioBank.Load(Path.Combine(output, "missing-audio")).Count == 0, "Missing audio fallback");
        return new { status = "passed", files = bank.Count, audioBytes = bytes, loopChecks, crossfadeSeconds = .8, priority = true,
            independentMute = true, peak, transitions, controls = true, devices, missingAssetFallback = true,
            subjectiveListening = "Not certified by automated checks; audition the supplied preview and game." };
    }

    private static object CheckTransitions()
    {
        using var audio = new GameAudio(false); audio.Volume(0, 100); var m = audio.Mixer; var events = new ScreenAudioEvents();
        var submission = new SubmissionClientSnapshot(SubmissionClientPhase.Writing, new("apple", "사과", "과일"), TimeSpan.FromSeconds(60), TimeSpan.Zero, null, false, null, null);
        var gallery = new RoundClientSnapshot(RoundClientPhase.AwaitingGallery, Array.AsReadOnly(Array.Empty<VotingArtwork>()), false, null, TimeSpan.Zero, null, null);
        var state = new RoundRuntimeSnapshot("me", true, submission, gallery, RoundAiPhase.Editing, false, null, "", null);
        var match = new MatchRuntimeSnapshot(Guid.NewGuid(), Guid.NewGuid(), 1, 2, true, MultiplayerMatchPhase.Playing, false, [], [], state, "");
        void Show() { events.Match(audio, match with { Round = state }); Advance(m, .12); }
        Show(); Show(); Require(m.Count(SoundCue.RoundStart) == 1, "Duplicate round start");
        for (int sec = 5; sec >= 1; sec--)
        {
            state = state with { Submission = submission with { WritingRemaining = TimeSpan.FromSeconds(sec) } };
            Show(); Show();
        }
        Require(m.Count(SoundCue.DeadlineTick) == 5, "Writing countdown duplicate/missing");
        foreach (var phase in new[] { SubmissionClientPhase.AwaitingApproval, SubmissionClientPhase.Generating, SubmissionClientPhase.SendingImage })
        { state = state with { Submission = submission with { Phase = phase, WritingRemaining = TimeSpan.FromSeconds(1), GenerationRemaining = TimeSpan.FromSeconds(1) } }; Show(); }
        Require(m.Count(SoundCue.DeadlineTick) == 5, "Submitted player heard countdown");
        state = state with { OwnPng = [1] }; Show(); Show(); Advance(m, .3);
        Require(m.Count(SoundCue.GenerationComplete) == 1, "Duplicate generation cue");
        gallery = gallery with { Phase = RoundClientPhase.Voting, CanVote = true, VotingRemaining = TimeSpan.FromSeconds(6) };
        state = state with { Gallery = gallery }; Show(); Show();
        Require(m.Count(SoundCue.GalleryOpen) == 1, "Duplicate gallery cue");
        for (int sec = 5; sec >= 1; sec--) { state = state with { Gallery = gallery with { VotingRemaining = TimeSpan.FromSeconds(sec) } }; Show(); Show(); }
        Require(m.Count(SoundCue.DeadlineTick) == 10, "Voting countdown duplicate/missing");
        state = state with { Gallery = gallery with { SelectedArtworkId = "peer", CanVote = false, VotingRemaining = TimeSpan.FromSeconds(1) } }; Show();
        Require(m.Count(SoundCue.DeadlineTick) == 10, "Voted player heard countdown");
        var result = new VotingResult(match.MatchId, match.RoundId, 1, 1, VotingEnd.AllVoted, DateTimeOffset.UtcNow,
            [new("a", "me", 1), new("b", "peer", 1)], ["a", "b"], 2, 2, 0);
        state = state with { Gallery = gallery with { Phase = RoundClientPhase.Completed, Result = result } }; Show(); Show();
        Require(m.Count(SoundCue.RoundResult) == 1, "Two-player tie result duplicate");
        Advance(m, 2); match = match with { RoundId = Guid.NewGuid(), RoundNumber = 2 };
        state = state with { OwnPng = null, Submission = submission, Gallery = gallery with { Phase = RoundClientPhase.AwaitingGallery, CanVote = false } }; Show();
        Require(m.Count(SoundCue.RoundStart) == 2, "Next round start missing");
        state = state with { OwnPng = [2], Gallery = gallery }; Show(); Advance(m, .5);
        Require(m.Count(SoundCue.GenerationComplete) == 1 && m.Count(SoundCue.GalleryOpen) == 2, "Simultaneous reveal priority");
        match = match with { Phase = MultiplayerMatchPhase.Completed }; state = state with { Gallery = gallery with { Result = result } }; Show(); Show();
        Require(m.Count(SoundCue.RoundResult) == 2, "Final round result cue");
        Advance(m, 1); events.Error(audio, "disconnected"); Advance(m, .5); events.Error(audio, "disconnected"); Advance(m, .5);
        Require(m.Count(SoundCue.ErrorNotice) == 1, "Persistent error repeated");
        var drawingEvents = new ScreenAudioEvents(); var drawing = new DrawingSnapshot(1, DrawingPhase.Editing, false, "", null, null) { FailureRevision = 1 };
        drawingEvents.Drawing(audio, drawing); Advance(m, .5); drawingEvents.Drawing(audio, drawing); Advance(m, .5);
        Require(m.Count(SoundCue.ErrorNotice) == 2, "Failure revision repeated");
        drawingEvents.Drawing(audio, drawing with { FailureRevision = 2 }); Advance(m, .5);
        Require(m.Count(SoundCue.ErrorNotice) == 3, "New identical failure not heard");
        events.Error(audio, "same request error", 1); Advance(m, .5);
        events.Error(audio, "same request error", 1); Advance(m, .5);
        events.Error(audio, "same request error", 2); Advance(m, .5);
        Require(m.Count(SoundCue.ErrorNotice) == 5, "Error request identity");
        match = match with { RoundId = Guid.NewGuid(), Phase = MultiplayerMatchPhase.Playing };
        state = state with { Submission = submission, Gallery = gallery with { Phase = RoundClientPhase.AwaitingGallery, CanVote = false },
            OwnPng = null, AiPhase = RoundAiPhase.Submitted, FailureRevision = 1 };
        Show(); Advance(m, .5);
        state = state with { Submission = submission with { Phase = SubmissionClientPhase.AwaitingGallery, Failure = "generation failed" } };
        Show(); Advance(m, .5); Show();
        Require(m.Count(SoundCue.ErrorNotice) == 6, "Host confirmation replayed generation failure");
        return m.Counts();
    }
}

public partial class MenuWindow
{
    internal Dictionary<string, int> AudioCounts => audio.Mixer.Counts();
    internal int AudioMusicChanges => audio.Mixer.MusicChanges;
    internal static async Task AudioControlsSmoke(string output)
    {
        using var sound = new GameAudio(false);
        var shell = new MenuWindow(new RuntimeAssets(Path.Combine(output, "no-models")), Path.Combine(output, "audio-settings.json"), sound: sound);
        shell.Show(); sound.Volume(100, 100);
        void Drain(double seconds = .2) => sound.Mixer.Render(new short[(int)(AudioBank.Rate * seconds)]);
        shell.Settings.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
        Require(sound.Mixer.Count(SoundCue.UiClick) == 1 && shell.SettingsOverlay.Visibility == Visibility.Visible, "Global click handler/settings");
        int changes = sound.Mixer.MusicChanges;
        shell.Bgm.Value = 0; shell.Sfx.Value = 0; Drain();
        var silent = new short[512]; sound.Mixer.Render(silent);
        Require(silent.All(v => v == 0) && sound.Mixer.Count(SoundCue.UiClick) == 1, "Slider preview/mute/no click");
        shell.ApplyClicked(shell, new()); shell.BackClicked(shell, new());
        Require(GameSettings.Load(shell.settingsPath).Bgm == 0 && GameSettings.Load(shell.settingsPath).Sfx == 0, "Muted settings persistence");
        shell.SettingsClicked(shell, new()); shell.Bgm.Value = 50; shell.Sfx.Value = 50; shell.BackClicked(shell, new()); Drain(); sound.Mixer.Render(silent);
        Require(silent.All(v => v == 0), "Unapplied volume not restored");
        sound.Volume(0, 100);
        var button = new Button(); shell.ScreenHost.Content = button;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
        Require(sound.Mixer.Count(SoundCue.UiClick) == 2, "Nested screen button click");
        button.IsEnabled = false; button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
        var repeat = new RepeatButton(); shell.ScreenHost.Content = repeat; repeat.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
        Require(sound.Mixer.Count(SoundCue.UiClick) == 2, "Disabled/slider repeat button sound");
        Require(sound.Mixer.MusicChanges == changes, "Settings restarted music");
        shell.ScreenHost.Content = null; shell.ExitClicked(shell, new()); await OnlineSmoke.Until(() => !shell.IsVisible);
    }
}
