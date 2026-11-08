using BGAReflowStudio.Models;
using Xunit;

namespace BGAReflowStudio.Tests;

public class ThermalProfileTests
{
    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        using var f = new TempFile();
        var p = new ThermalProfile
        {
            Name = "Reflow A",
            Holdback = 12, PowerBottomIndex = 4,
            CritTemp = 231, CritOffset = 8, CritDynamic = true,
            SoundAlarm = 221, AlarmOffset = 4, SoundAlarmDynamic = true,
            Bottom40 = 31, Bottom60 = 71, Bottom80 = 121, Bottom100 = 161,
            TimeAlarm = 5, AutoBottom = 7,
            Check40 = true, Check60 = false, Check80 = true, Check100 = false,
        };
        p.Ramp[0] = 0.45m; p.Target[0] = 150; p.Dwell[0] = 90;
        p.Ramp[7] = 1.25m; p.Target[7] = 205; p.Dwell[7] = 15;
        p.Save(3, f.Path);

        var q = ThermalProfile.Load(3, f.Path);

        Assert.Equal("Reflow A", q.Name);
        Assert.Equal(12m, q.Holdback);
        Assert.Equal(4, q.PowerBottomIndex);
        Assert.Equal(231m, q.CritTemp);
        Assert.Equal(8m, q.CritOffset);
        Assert.True(q.CritDynamic);
        Assert.Equal(221m, q.SoundAlarm);
        Assert.Equal(4m, q.AlarmOffset);
        Assert.True(q.SoundAlarmDynamic);
        Assert.Equal(31m, q.Bottom40);
        Assert.Equal(161m, q.Bottom100);
        Assert.Equal(5m, q.TimeAlarm);
        Assert.Equal(7m, q.AutoBottom);
        Assert.True(q.Check40);
        Assert.False(q.Check60);
        Assert.Equal(0.45m, q.Ramp[0]);
        Assert.Equal(150m, q.Target[0]);
        Assert.Equal(90m, q.Dwell[0]);
        Assert.Equal(1.25m, q.Ramp[7]);
        Assert.Equal(205m, q.Target[7]);
    }

    [Fact]
    public void Load_MissingSafetyKeys_UsesDefaults()
    {
        using var f = new TempFile();
        IniFile.Write("0", "Name", "Bare", f.Path);   // a section with no safety keys

        var q = ThermalProfile.Load(0, f.Path);

        Assert.Equal(230m, q.CritTemp);     // default static critical
        Assert.Equal(15m, q.CritOffset);    // default dynamic critical
        Assert.Equal(225m, q.SoundAlarm);   // default static alarm
        Assert.Equal(10m, q.AlarmOffset);   // default dynamic alarm
        Assert.False(q.CritDynamic);
        Assert.False(q.SoundAlarmDynamic);
    }

    [Fact]
    public void Load_UnknownSection_IsEmptyRecipe()
    {
        using var f = new TempFile();
        IniFile.Write("0", "Name", "x", f.Path);
        var q = ThermalProfile.Load(9, f.Path);   // section [9] doesn't exist
        for (int i = 0; i < ThermalProfile.StepCount; i++)
        {
            Assert.Equal(0m, q.Ramp[i]);
            Assert.Equal(0m, q.Target[i]);
            Assert.Equal(0m, q.Dwell[i]);
        }
    }
}
