using BGAReflowStudio.Models;
using Xunit;

namespace BGAReflowStudio.Tests;

public class ReflowCurveTests
{
    private static ThermalProfile OneStep()
    {
        // Step 1: ramp 1 °C/s to 120 °C, dwell 60 s; then end.
        var p = new ThermalProfile();
        p.Ramp[0] = 1.0m; p.Target[0] = 120; p.Dwell[0] = 60;
        p.Ramp[1] = ThermalProfile.EndSentinel;
        return p;
    }

    [Fact]
    public void Build_StartsAtAmbient()
    {
        var pts = ReflowCurve.Build(OneStep()).Points;
        Assert.Equal((0d, (double)ReflowCurve.Ambient), pts[0]);
    }

    [Fact]
    public void Build_SingleStep_RampThenDwell()
    {
        var pts = ReflowCurve.Build(OneStep()).Points;
        // ambient -> ramp end -> dwell end
        Assert.Equal(3, pts.Count);
        Assert.Equal((0d, 20d), pts[0]);
        Assert.Equal((100d, 120d), pts[1]);   // (120-20)/1 = 100 s
        Assert.Equal((160d, 120d), pts[2]);   // + 60 s dwell
    }

    [Fact]
    public void Build_EndSentinel_SetsBottomContinue()
    {
        // bottomContinue = round(prevTemp) - 25
        Assert.Equal(95, ReflowCurve.Build(OneStep()).BottomContinue);
    }

    [Fact]
    public void Build_EndSentinel_TruncatesLaterSteps()
    {
        var p = OneStep();
        // A step beyond the sentinel must be ignored.
        p.Ramp[2] = 2.0m; p.Target[2] = 300; p.Dwell[2] = 99;
        var pts = ReflowCurve.Build(p).Points;
        Assert.Equal(3, pts.Count);
        Assert.DoesNotContain(pts, pt => pt.Temp == 300d);
    }

    [Fact]
    public void Build_RampZero_SkipsThatStepsSegment()
    {
        var p = new ThermalProfile();
        p.Ramp[0] = 1.0m; p.Target[0] = 120; p.Dwell[0] = 60;   // -> 3 points
        p.Ramp[1] = 0m;   p.Target[1] = 150; p.Dwell[1] = 30;   // skipped entirely
        p.Ramp[2] = 2.0m; p.Target[2] = 200; p.Dwell[2] = 30;   // -> 2 more points
        p.Ramp[3] = ThermalProfile.EndSentinel;
        var pts = ReflowCurve.Build(p).Points;

        Assert.Equal(5, pts.Count);                         // 1 ambient + 2 + 2
        Assert.DoesNotContain(pts, pt => pt.Temp == 150d);  // the ramp-0 step contributes nothing
        Assert.Equal((200d, 200d), pts[3]);                 // (200-120)/2 = 40 s after 160 s
        Assert.Equal((230d, 200d), pts[4]);                 // + 30 s dwell
    }
}
