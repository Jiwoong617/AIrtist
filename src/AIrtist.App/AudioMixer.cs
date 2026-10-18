using System.IO;
using System.Text;

namespace AIrtist.App;

internal enum MusicTrack { None, Menu, Game }
internal enum SoundCue { UiClick, RoundStart, GenerationComplete, GalleryOpen, DeadlineTick, RoundResult, ErrorNotice }
internal sealed record AudioClip(float[] Samples);

internal static class AudioBank
{
    internal const int Rate = 22050;
    internal static string FileName(SoundCue cue) => cue switch
    {
        SoundCue.UiClick => "ui_click", SoundCue.RoundStart => "round_start",
        SoundCue.GenerationComplete => "generation_complete", SoundCue.GalleryOpen => "gallery_open",
        SoundCue.DeadlineTick => "deadline_tick", SoundCue.RoundResult => "round_result", _ => "error_notice"
    };
    internal static Dictionary<string, AudioClip> Load(string directory)
    {
        var clips = new Dictionary<string, AudioClip>();
        foreach (string id in new[] { "bgm_menu", "bgm_game" }.Concat(Enum.GetValues<SoundCue>().Select(FileName)))
        {
            try { clips[id] = Read(Path.Combine(directory, id + ".wav")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return clips;
    }
    internal static AudioClip Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length is < 44 or > 6_000_000) throw new InvalidDataException("Invalid audio size");
        using var reader = new BinaryReader(file, Encoding.ASCII);
        string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (Tag() != "RIFF" || reader.ReadUInt32() != file.Length - 8 || Tag() != "WAVE") throw new InvalidDataException("Invalid WAV");
        bool format = false;
        while (file.Position + 8 <= file.Length)
        {
            string tag = Tag(); uint size = reader.ReadUInt32(); long end = file.Position + size;
            if (end > file.Length) throw new InvalidDataException("Truncated audio");
            if (tag == "fmt ")
            {
                if (size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 || reader.ReadUInt32() != Rate ||
                    reader.ReadUInt32() != Rate * 2 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("Unsupported PCM format");
                format = true;
            }
            else if (tag == "data")
            {
                if (!format || size == 0 || size % 2 != 0) throw new InvalidDataException("Invalid PCM");
                var samples = new float[size / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16() / 32768f;
                return new(samples);
            }
            file.Position = end + size % 2;
        }
        throw new InvalidDataException("Missing PCM");
    }
}

// A continuous PCM timeline owns loop positions, fades and cue scheduling.
// No media-ended callbacks or UI timers are involved in the audio stream.
internal sealed class AudioMixer
{
    private readonly object sync = new();
    private Dictionary<string, AudioClip> bank = new();
    private readonly List<Voice> voices = [];
    private readonly List<(SoundCue Cue, long At)> pending = [];
    private readonly Dictionary<SoundCue, long> lastCue = new();
    private readonly Dictionary<SoundCue, int> played = new();
    private MusicTrack selected;
    private int menuPosition, gamePosition, fadeLeft;
    private float menuGain, gameGain, menuStart, gameStart;
    private float bgm = .7f, sfx = .7f;
    private long frame;
    internal const int FadeFrames = AudioBank.Rate * 8 / 10;
    private sealed class Voice(SoundCue cue, AudioClip clip)
    {
        public readonly SoundCue Cue = cue;
        public readonly AudioClip Clip = clip;
        public int Position;
        public int Release = -1;
    }
    internal void Load(Dictionary<string, AudioClip> clips) { lock (sync) bank = clips; }
    internal int LoadedCount { get { lock (sync) return bank.Count; } }
    internal MusicTrack Selected { get { lock (sync) return selected; } }
    internal int MusicChanges { get; private set; }
    internal long RenderedFrames { get { lock (sync) return frame; } }
    internal int Count(SoundCue cue) { lock (sync) return played.GetValueOrDefault(cue); }
    internal Dictionary<string, int> Counts() { lock (sync) return played.ToDictionary(p => AudioBank.FileName(p.Key), p => p.Value); }
    internal (int Menu, int Game, float MenuGain, float GameGain) MusicState
    { get { lock (sync) return (menuPosition, gamePosition, menuGain, gameGain); } }
    internal void SetVolumes(int music, int effects)
    {
        lock (sync)
        {
            bgm = Math.Clamp(music, 0, 100) / 100f; sfx = Math.Clamp(effects, 0, 100) / 100f;
            if (sfx == 0) { voices.Clear(); pending.Clear(); }
        }
    }
    internal void Music(MusicTrack track)
    {
        lock (sync)
        {
            if (track == selected) return;
            selected = track; MusicChanges++;
            menuStart = menuGain; gameStart = gameGain; fadeLeft = FadeFrames;
            // Keep the common click, discard cues from the screen being left.
            voices.RemoveAll(v => v.Cue != SoundCue.UiClick); pending.RemoveAll(p => p.Cue != SoundCue.UiClick);
        }
    }
    internal void Cue(SoundCue cue)
    {
        lock (sync)
        {
            if (sfx == 0 || !bank.ContainsKey(AudioBank.FileName(cue))) return;
            int cooldown = cue == SoundCue.UiClick ? 60 : cue == SoundCue.ErrorNotice ? 400 : 100;
            if (lastCue.TryGetValue(cue, out long last) && frame - last < AudioBank.Rate * cooldown / 1000) return;
            lastCue[cue] = frame;
            if (cue == SoundCue.GalleryOpen) Suppress(SoundCue.GenerationComplete);
            if (cue == SoundCue.RoundResult) Suppress(SoundCue.DeadlineTick);
            if (cue == SoundCue.GenerationComplete && lastCue.TryGetValue(SoundCue.GalleryOpen, out last) && frame - last < AudioBank.Rate / 4) return;
            if (cue == SoundCue.DeadlineTick && lastCue.TryGetValue(SoundCue.RoundResult, out last) && frame - last < AudioBank.Rate / 2) return;
            pending.RemoveAll(p => p.Cue == cue);
            pending.Add((cue, frame + (cue == SoundCue.GenerationComplete ? AudioBank.Rate / 5 : 0)));
        }
    }
    private void Suppress(SoundCue cue)
    {
        pending.RemoveAll(p => p.Cue == cue);
        foreach (var voice in voices.Where(v => v.Cue == cue)) voice.Release = AudioBank.Rate / 100;
    }
    internal void Render(short[] output)
    {
        lock (sync)
        {
            bank.TryGetValue("bgm_menu", out var menu); bank.TryGetValue("bgm_game", out var game);
            for (int i = 0; i < output.Length; i++, frame++)
            {
                for (int j = pending.Count - 1; j >= 0; j--)
                {
                    var p = pending[j]; if (p.At > frame) continue;
                    pending.RemoveAt(j);
                    if (voices.Count >= 3) voices.RemoveAt(0);
                    voices.Add(new(p.Cue, bank[AudioBank.FileName(p.Cue)]));
                    played[p.Cue] = played.GetValueOrDefault(p.Cue) + 1;
                }
                if (fadeLeft > 0)
                {
                    float progress = 1 - --fadeLeft / (float)FadeFrames;
                    menuGain = menuStart + ((selected == MusicTrack.Menu ? 1 : 0) - menuStart) * progress;
                    gameGain = gameStart + ((selected == MusicTrack.Game ? 1 : 0) - gameStart) * progress;
                }
                float value = 0;
                if (menu is not null && (menuGain > 0 || selected == MusicTrack.Menu))
                { value += menu.Samples[menuPosition] * menuGain * bgm; menuPosition = (menuPosition + 1) % menu.Samples.Length; }
                if (game is not null && (gameGain > 0 || selected == MusicTrack.Game))
                { value += game.Samples[gamePosition] * gameGain * bgm; gamePosition = (gamePosition + 1) % game.Samples.Length; }
                for (int j = voices.Count - 1; j >= 0; j--)
                {
                    var v = voices[j];
                    if (v.Position >= v.Clip.Samples.Length || v.Release == 0) { voices.RemoveAt(j); continue; }
                    float release = v.Release < 0 ? 1 : v.Release-- / (float)(AudioBank.Rate / 100);
                    value += v.Clip.Samples[v.Position++] * sfx * release;
                }
                output[i] = (short)Math.Round(.85f * MathF.Tanh(value / .85f) * short.MaxValue);
            }
        }
    }
}
