using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

namespace BGAReflowStudio.Models;

/// <summary>
/// An 8-step thermal reflow profile plus per-profile run settings. Each profile is one
/// <c>[0]..[9]</c> section of the profiles file. Step arrays are 0-based (index 0 ==
/// "step 1"). Ramp of <c>-0.01</c> means "end the profile here"; ramp of <c>0</c> means
/// "skip this step's ramp segment".
/// </summary>
public sealed class ThermalProfile
{
    public const int StepCount = 8;
    public const decimal EndSentinel = -0.01m;

    public string Name { get; set; } = "";

    public decimal[] Ramp { get; } = new decimal[StepCount];   // °C/s
    public decimal[] Target { get; } = new decimal[StepCount]; // °C
    public decimal[] Dwell { get; } = new decimal[StepCount];  // s
    public decimal Holdback { get; set; }

    public int PowerBottomIndex { get; set; }                  // 0..9 -> 10..100 %
    public decimal CritTemp { get; set; }                      // process auto-stop temp (static value)
    public decimal CritOffset { get; set; }                    // dynamic: °C added to the set's highest target
    public bool CritDynamic { get; set; }                      // false = static, true = dynamic
    public decimal SoundAlarm { get; set; }                    // audible-alarm trigger temp (static value)
    public decimal AlarmOffset { get; set; }                   // dynamic: °C added to the set's highest target
    public bool SoundAlarmDynamic { get; set; }                // false = static, true = dynamic
    public decimal Bottom40 { get; set; }                      // bottom-power staging thresholds
    public decimal Bottom60 { get; set; }
    public decimal Bottom80 { get; set; }
    public decimal Bottom100 { get; set; }
    public decimal TimeAlarm { get; set; }                     // alarm auto-silence (ticks)
    public decimal AutoBottom { get; set; }                    // Auto-mode setpoint boost
    public bool Check40 { get; set; }
    public bool Check60 { get; set; }
    public bool Check80 { get; set; }
    public bool Check100 { get; set; }

    public static ThermalProfile Load(int index, string path)
    {
        var s = index.ToString();
        var p = new ThermalProfile { Name = IniFile.Read(s, "Name", path, index.ToString()) };

        for (int i = 0; i < StepCount; i++)
        {
            p.Ramp[i] = Dec(IniFile.Read(s, $"Ramp{i + 1}", path));
            p.Target[i] = Dec(IniFile.Read(s, $"Target{i + 1}", path));
            p.Dwell[i] = Dec(IniFile.Read(s, $"Dwell{i + 1}", path));
        }

        p.Holdback = Dec(IniFile.Read(s, "Holdback", path));
        p.PowerBottomIndex = Int(IniFile.Read(s, "BottomPower", path));
        if (p.PowerBottomIndex < 0) p.PowerBottomIndex = 0;
        p.CritTemp = DecOr(IniFile.Read(s, "CriticalTemp", path), 230m);
        p.CritOffset = DecOr(IniFile.Read(s, "CriticalOffset", path), 15m);
        p.CritDynamic = Bool(IniFile.Read(s, "CriticalDynamic", path));
        p.SoundAlarm = DecOr(IniFile.Read(s, "SoundAlarmTemp", path), 225m);
        p.AlarmOffset = DecOr(IniFile.Read(s, "SoundAlarmOffset", path), 10m);
        p.SoundAlarmDynamic = Bool(IniFile.Read(s, "SoundAlarmDynamic", path));
        p.Bottom40 = Dec(IniFile.Read(s, "BottomThreshold40", path));
        p.Bottom60 = Dec(IniFile.Read(s, "BottomThreshold60", path));
        p.Bottom80 = Dec(IniFile.Read(s, "BottomThreshold80", path));
        p.Bottom100 = Dec(IniFile.Read(s, "BottomThreshold100", path));
        p.TimeAlarm = Dec(IniFile.Read(s, "AlarmSilenceTicks", path));
        p.AutoBottom = Dec(IniFile.Read(s, "AutoBottomBoost", path));
        p.Check40 = Bool(IniFile.Read(s, "Stage40", path));
        p.Check60 = Bool(IniFile.Read(s, "Stage60", path));
        p.Check80 = Bool(IniFile.Read(s, "Stage80", path));
        p.Check100 = Bool(IniFile.Read(s, "Stage100", path));
        return p;
    }

    public void Save(int index, string path)
    {
        var s = index.ToString();
        for (int i = 0; i < StepCount; i++)
        {
            IniFile.Write(s, $"Ramp{i + 1}", Ramp[i].ToString(), path);
            IniFile.Write(s, $"Target{i + 1}", Target[i].ToString(), path);
            IniFile.Write(s, $"Dwell{i + 1}", Dwell[i].ToString(), path);
        }

        IniFile.Write(s, "Holdback", Holdback.ToString(), path);
        IniFile.Write(s, "BottomPower", PowerBottomIndex.ToString(), path);
        IniFile.Write(s, "Name", Name, path);
        IniFile.Write(s, "CriticalTemp", CritTemp.ToString(), path);
        IniFile.Write(s, "CriticalOffset", CritOffset.ToString(), path);
        IniFile.Write(s, "CriticalDynamic", CritDynamic.ToString(), path);
        IniFile.Write(s, "BottomThreshold40", Bottom40.ToString(), path);
        IniFile.Write(s, "BottomThreshold60", Bottom60.ToString(), path);
        IniFile.Write(s, "BottomThreshold80", Bottom80.ToString(), path);
        IniFile.Write(s, "BottomThreshold100", Bottom100.ToString(), path);
        IniFile.Write(s, "SoundAlarmTemp", SoundAlarm.ToString(), path);
        IniFile.Write(s, "SoundAlarmOffset", AlarmOffset.ToString(), path);
        IniFile.Write(s, "SoundAlarmDynamic", SoundAlarmDynamic.ToString(), path);
        IniFile.Write(s, "AlarmSilenceTicks", TimeAlarm.ToString(), path);
        IniFile.Write(s, "AutoBottomBoost", AutoBottom.ToString(), path);
        IniFile.Write(s, "Stage40", Check40.ToString(), path);
        IniFile.Write(s, "Stage60", Check60.ToString(), path);
        IniFile.Write(s, "Stage80", Check80.ToString(), path);
        IniFile.Write(s, "Stage100", Check100.ToString(), path);
    }

    // Use the VB runtime conversions so comma/period handling matches the controller.
    private static decimal Dec(string v)
    {
        try { return v.Length == 0 ? 0m : Conversions.ToDecimal(v); }
        catch { return 0m; }
    }

    private static decimal DecOr(string v, decimal fallback) => v.Length == 0 ? fallback : Dec(v);

    private static int Int(string v)
    {
        try { return (int)Math.Round(Conversion.Val(v)); }
        catch { return 0; }
    }

    private static bool Bool(string v) =>
        v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1";
}
