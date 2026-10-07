namespace AIrtist.Core;

// Null Seed means random at approval/start, never a mutable global generator setting.
public sealed record GenerationParameters(float Guidance = 7f, int Steps = 25, int? Seed = null)
{
    public bool IsValid() => float.IsFinite(Guidance) && Guidance is >= 1f and <= 20f
        && Steps is >= 1 and <= 50 && (Seed is null || Seed >= 0);
    public void Validate()
    {
        if (!IsValid()) throw new ArgumentException("CFG는 1~20, Steps는 1~50, Seed는 0~2147483647로 입력해 주세요.");
    }
    public GenerationParameters Resolve()
    {
        Validate(); return Seed is null ? this with { Seed = Random.Shared.Next() } : this;
    }
    public bool Accepts(GenerationParameters? resolved) => resolved is not null && resolved.IsValid()
        && resolved.Seed is not null && Guidance == resolved.Guidance && Steps == resolved.Steps
        && (Seed is null || Seed == resolved.Seed);
}
