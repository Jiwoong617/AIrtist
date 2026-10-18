using System.IO;
using AIrtist.Core;

namespace AIrtist.App;

internal sealed class GameAudio : IDisposable
{
    internal AudioMixer Mixer { get; } = new();
    internal WaveAudioOutput? Output { get; }
    private bool disposed;
    internal GameAudio(bool hardware = true, string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "audio");
        if (hardware) Output = new(Mixer, directory);
        else Mixer.Load(AudioBank.Load(directory));
    }
    internal void Music(MusicTrack track) { if (!disposed) Mixer.Music(track); }
    internal void Cue(SoundCue cue) { if (!disposed) Mixer.Cue(cue); }
    internal void Volume(int bgm, int sfx) { if (!disposed) Mixer.SetVolumes(bgm, sfx); }
    public void Dispose() { if (disposed) return; disposed = true; Output?.Dispose(); }
}

// One observer per screen/local player. Repeated snapshots never replay a cue.
internal sealed class ScreenAudioEvents
{
    private Guid round;
    private string? player, error;
    private bool started, ownImage, gallery, result, generationFailure;
    private int writingLowest = 6, votingLowest = 6;
    private long drawingFailure, roundFailure;
    private DrawingResult? drawingResult;
    private long errorRevision = -1;
    private SubmissionStatus? submissionStatus;
    internal void Error(GameAudio audio, string? value, long revision = 0)
    {
        if (!string.IsNullOrEmpty(value) && (value != error || errorRevision != revision)) audio.Cue(SoundCue.ErrorNotice);
        error = value; errorRevision = revision;
    }
    internal void Drawing(GameAudio audio, DrawingSnapshot state)
    {
        if (state.Result is { } image && !ReferenceEquals(drawingResult, image))
        { drawingResult = image; audio.Cue(SoundCue.GenerationComplete); }
        if (state.FailureRevision > drawingFailure) { drawingFailure = state.FailureRevision; audio.Cue(SoundCue.ErrorNotice); }
    }
    internal void Match(GameAudio audio, MatchRuntimeSnapshot match)
    {
        if (match.Round is not { } state) return;
        if (round != match.RoundId || player != state.LocalId)
        {
            round = match.RoundId; player = state.LocalId;
            started = ownImage = gallery = result = generationFailure = false;
            writingLowest = votingLowest = 6; roundFailure = 0; error = null; submissionStatus = null;
        }
        if (match.Phase == MultiplayerMatchPhase.Stopped)
        { Error(audio, match.Message); return; }
        if (!started && state.Submission.Prompt is not null && match.Phase == MultiplayerMatchPhase.Playing && state.Gallery.Result is null)
        { started = true; audio.Cue(SoundCue.RoundStart); }
        bool showingGallery = state.Gallery.Phase is RoundClientPhase.Voting or RoundClientPhase.Completed;
        if (state.Gallery.Result is not null)
        {
            if (!result) { result = true; audio.Cue(SoundCue.RoundResult); }
            gallery = true; ownImage |= state.OwnPng is not null;
        }
        else if (showingGallery)
        {
            if (!gallery) { gallery = true; audio.Cue(SoundCue.GalleryOpen); }
            ownImage |= state.OwnPng is not null;
        }
        else if (!ownImage && state.OwnPng is not null)
        { ownImage = true; audio.Cue(SoundCue.GenerationComplete); }
        bool failedGeneration = state.AiPhase == RoundAiPhase.Submitted && state.OwnPng is null;
        if (state.FailureRevision > roundFailure)
        {
            roundFailure = state.FailureRevision;
            if (!failedGeneration || !generationFailure) audio.Cue(SoundCue.ErrorNotice);
            generationFailure |= failedGeneration;
        }
        // The host later confirms a local generation failure; it is the same incident.
        string? failure = state.Gallery.Failure ?? (failedGeneration && generationFailure ? null : state.Submission.Failure);
        Error(audio, failure);
        generationFailure |= failedGeneration && state.Submission.Failure is not null;
        if (state.Submission.LastSubmissionStatus is { } status && status != submissionStatus &&
            status is not (SubmissionStatus.Accepted or SubmissionStatus.Duplicate)) audio.Cue(SoundCue.ErrorNotice);
        submissionStatus = state.Submission.LastSubmissionStatus;
        if (match.Phase != MultiplayerMatchPhase.Playing || result) return;
        // Translation still consumes writing time; an already submitted/approved request does not tick.
        if (state.Submission.Phase == SubmissionClientPhase.Writing && !showingGallery)
            Countdown(audio, state.Submission.WritingRemaining, ref writingLowest);
        if (state.Gallery.CanVote && state.Gallery.SelectedArtworkId is null)
            Countdown(audio, state.Gallery.VotingRemaining, ref votingLowest);
    }
    private static void Countdown(GameAudio audio, TimeSpan remaining, ref int lowest)
    {
        int seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds is >= 1 and <= 5 && seconds < lowest) { lowest = seconds; audio.Cue(SoundCue.DeadlineTick); }
    }
}
