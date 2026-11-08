namespace BGAReflowStudio.Models;

/// <summary>
/// Builds the predicted "Set Temperature" reference curve from a profile. Starts at
/// ambient (20 °C); for each active step it ramps from the previous target to this
/// step's target at the step's ramp rate, then holds for the dwell time. A ramp of
/// -0.01 ends the curve; a ramp of 0 skips that step's ramp segment.
/// </summary>
public static class ReflowCurve
{
    public const int Ambient = 20;

    public sealed record Result(List<(double Time, double Temp)> Points, int BottomContinue);

    public static Result Build(ThermalProfile p)
    {
        var pts = new List<(double, double)>();
        int bottomContinue = 0;

        double time = 0;
        pts.Add((time, Ambient));

        // Step 1 always plots (ramp from ambient to target1, then dwell1).
        double temp = (double)p.Target[0];
        double ramp0 = (double)p.Ramp[0];
        time = ramp0 != 0 ? Math.Round((temp - Ambient) / ramp0, 0) : time;
        pts.Add((time, temp));
        time = Math.Round(time + (double)p.Dwell[0], 0);
        pts.Add((time, temp));

        double prevTemp = temp;

        // Steps 2..8.
        for (int i = 1; i < ThermalProfile.StepCount; i++)
        {
            if (p.Ramp[i] == ThermalProfile.EndSentinel)
            {
                bottomContinue = (int)Math.Round(prevTemp) - 25;
                return new Result(pts, bottomContinue);
            }

            if (p.Ramp[i] == 0m)
                continue;

            double target = (double)p.Target[i];
            double dt = Math.Abs(target - prevTemp) / (double)p.Ramp[i];
            time = Math.Round(time + dt, 0);
            pts.Add((time, target));
            time = Math.Round(time + (double)p.Dwell[i], 0);
            pts.Add((time, target));
            prevTemp = target;
        }

        return new Result(pts, bottomContinue);
    }
}
