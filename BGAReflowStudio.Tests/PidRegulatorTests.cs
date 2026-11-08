using BGAReflowStudio.Models;
using Xunit;

namespace BGAReflowStudio.Tests;

public class PidRegulatorTests
{
    [Theory]
    [InlineData(-5f, 0)]
    [InlineData(0f, 0)]
    [InlineData(15f, 0)]
    [InlineData(15.01f, 1)]
    [InlineData(55f, 4)]
    [InlineData(95f, 8)]
    [InlineData(95.01f, 9)]
    [InlineData(100f, 9)]
    [InlineData(1000f, 9)]
    public void CvToPowerIndex_MapsBands(float cv, int expected)
        => Assert.Equal(expected, PidRegulator.CvToPowerIndex(cv));

    [Fact]
    public void Step_ForcesFullPower_WhenTopExceedsBottomContinue()
    {
        var pid = new PidRegulator();
        pid.Reset();
        // topTemp (300) > bottomContinue (100) must clamp the control value to 100.
        float cv = pid.Step(setPoint: 150, autoBottom: 0m, pv2: 40, topTemp: 300, bottomContinue: 100);
        Assert.Equal(100f, cv);
    }

    [Fact]
    public void Step_ReturnsWithinBounds()
    {
        var pid = new PidRegulator();
        pid.Reset();
        for (int pv = 0; pv <= 200; pv += 20)
        {
            float cv = pid.Step(setPoint: 150, autoBottom: 5m, pv2: pv, topTemp: 120, bottomContinue: 200);
            Assert.InRange(cv, 0f, 100f);
        }
    }
}
