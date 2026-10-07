namespace AIrtist.Core;

public sealed record SchedulerData(string Source, string Algorithm, ScheduleStep[] Steps);
public sealed record ScheduleStep(int Timestep, float Alpha, float Sigma, float Sample, float Current, float Previous);

public sealed class DpmScheduler(SchedulerData data)
{
    // Tiny-SD scaled-linear beta schedule and DPM-Solver++ order 2 midpoint.
    // Match Diffusers linspace/round-to-even timesteps; the terminal sigma is zero.
    // The packaged 25-step table is retained by LocalGenerator for exact baseline behavior.
    public static SchedulerData CreateSchedule(int count)
    {
        new GenerationParameters(Steps: count).Validate();
        var alpha = new double[1000]; var sigma = new double[1000];
        double product = 1, lo = Math.Sqrt(0.00085), hi = Math.Sqrt(0.012);
        for (int t = 0; t < 1000; t++)
        {
            double betaRoot = lo + (hi - lo) * t / 999;
            product *= 1 - betaRoot * betaRoot;
            alpha[t] = Math.Sqrt(product); sigma[t] = Math.Sqrt(1 - product);
        }
        int[] times = Enumerable.Range(0, count).Select(i => (int)Math.Round(999d * (count - i) / count)).ToArray();
        var steps = new ScheduleStep[count];
        double previousLambda = 0;
        for (int i = 0; i < count; i++)
        {
            int t = times[i]; double a = alpha[t], s = sigma[t], lambda = Math.Log(a / s);
            if (i == count - 1) steps[i] = new(t, (float)a, (float)s, 0, 1, 0);
            else
            {
                int next = times[i + 1];
                double h = Math.Log(alpha[next] / sigma[next]) - lambda;
                double current = alpha[next] * (1 - Math.Exp(-h));
                double correction = i == 0 ? 0 : current * h / (2 * (lambda - previousLambda));
                steps[i] = new(t, (float)a, (float)s, (float)(sigma[next] / s),
                    (float)(current + correction), (float)-correction);
            }
            previousLambda = lambda;
        }
        return new("Tiny-SD scaled_linear 0.00085..0.012 / 1000", "dpmsolver++/order2/midpoint", steps);
    }

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
