namespace AIrtist.Core;

public sealed record SchedulerData(string Source, string Algorithm, ScheduleStep[] Steps);
public sealed record ScheduleStep(int Timestep, float Alpha, float Sigma, float Sample, float Current, float Previous);

public sealed class DpmScheduler(SchedulerData data)
{
    private float[]? previous;
    public ScheduleStep[] Steps { get; } = data.Steps;

    public float[] Step(int index, float[] sample, float[] noise)
    {
        var s = Steps[index];
        var current = new float[sample.Length];
        var result = new float[sample.Length];
        for (int j = 0; j < sample.Length; j++)
        {
            current[j] = (sample[j] - s.Sigma * noise[j]) / s.Alpha;
            result[j] = s.Sample * sample[j] + s.Current * current[j] + s.Previous * (previous?[j] ?? 0);
            if (!float.IsFinite(result[j])) throw new InvalidDataException("이미지 생성 중 비정상적인 값이 발생했습니다.");
        }
        previous = current;
        return result;
    }
}
