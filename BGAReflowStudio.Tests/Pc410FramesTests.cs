using System;
using System.Linq;
using BGAReflowStudio.Protocol;
using Xunit;

namespace BGAReflowStudio.Tests;

/// <summary>
/// Golden-vector tests for the PC410 wire frames. Most vectors are copied directly from a real
/// serial.log captured against hardware; the rest are hand-computed BCCs. If any of these change,
/// the controller has stopped speaking the PC410's protocol.
/// </summary>
public class Pc410FramesTests
{
    private static byte[] B(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

    // ---- polls (EOT "0011" <name> ENQ, no BCC) ----
    [Theory]
    [InlineData("A2", "04 30 30 31 31 41 32 05")]
    [InlineData("PV", "04 30 30 31 31 50 56 05")]
    [InlineData("#3", "04 30 30 31 31 23 33 05")]
    [InlineData("ch", "04 30 30 31 31 63 68 05")]
    public void Poll_MatchesWire(string name, string hex) => Assert.Equal(B(hex), Pc410Frames.Poll(name));

    // ---- ramp writes (value INCLUDES the VB leading space/sign) ----
    [Fact]
    public void Ramp_1_1point00() =>
        Assert.Equal(B("04 30 30 31 31 02 72 31 20 31 2E 30 30 03 7F"), Pc410Frames.Ramp(1, 1.00m));

    [Fact]
    public void Ramp_6_EndSentinel() =>   // -0.01 end marker, from a real capture
        Assert.Equal(B("04 30 30 31 31 02 72 36 2D 30 2E 30 31 03 75"), Pc410Frames.Ramp(6, -0.01m));

    // ---- level / dwell / holdback (value WITHOUT the leading space) ----
    [Fact]
    public void Level_1_30() =>
        Assert.Equal(B("04 30 30 31 31 02 6C 31 33 30 03 5D"), Pc410Frames.Level(1, 30m));

    [Fact]
    public void Dwell_1_300() =>
        Assert.Equal(B("04 30 30 31 31 02 74 31 33 30 30 03 75"), Pc410Frames.Dwell(1, 300m));

    [Fact]
    public void Holdback_0() =>
        Assert.Equal(B("04 30 30 31 31 02 48 62 30 03 19"), Pc410Frames.Holdback(0m));

    [Fact]
    public void Holdback_15() =>
        Assert.Equal(B("04 30 30 31 31 02 48 62 31 35 03 2D"), Pc410Frames.Holdback(15m));

    // ---- channel select ----
    [Theory]
    [InlineData(0, "04 30 30 31 31 02 63 68 30 03 38")]
    [InlineData(1, "04 30 30 31 31 02 63 68 31 03 39")]
    public void SelectChannel_MatchesWire(int index, string hex) =>
        Assert.Equal(B(hex), Pc410Frames.SelectChannel(index));

    // ---- control / status frames (fixed bodies with precomputed BCC) ----
    [Fact] public void Start_Frame() => Assert.Equal(B("04 30 30 31 31 02 23 33 30 30 30 31 03 12"), Pc410Frames.Start());
    [Fact] public void Stop_Frame()  => Assert.Equal(B("04 30 30 31 31 02 23 33 30 30 30 30 03 13"), Pc410Frames.Stop());
    [Fact] public void Hold_Frame()  => Assert.Equal(B("04 30 30 31 31 02 23 33 30 30 30 32 03 11"), Pc410Frames.Hold());
    [Fact] public void AlarmStart_Frame() => Assert.Equal(B("04 30 30 31 31 02 41 32 30 30 31 03 41"), Pc410Frames.AlarmStart());
    [Fact] public void AlarmStop_Frame()  => Assert.Equal(B("04 30 30 31 31 02 41 32 35 30 30 03 45"), Pc410Frames.AlarmStop());

    // ---- structural invariants ----
    [Fact]
    public void Xor_IsXorOfRange()
    {
        var buf = new byte[] { 0x10, 0x20, 0x30, 0xFF };
        Assert.Equal((byte)(0x10 ^ 0x20 ^ 0x30), Pc410Frames.Xor(buf, 0, 3));
    }

    [Theory]
    [InlineData("A2")]
    [InlineData("PV")]
    public void Poll_StartsWithSelectPreamble_AndEndsWithEnq(string name)
    {
        var f = Pc410Frames.Poll(name);
        Assert.Equal(B("04 30 30 31 31"), f.Take(5).ToArray());   // EOT "0011"
        Assert.Equal(0x05, f[^1]);                                // ENQ
    }

    [Fact]
    public void ValueFrame_BccIsXorOfNameValueEtx()
    {
        // Independent check of the block-check on a level write: BCC == XOR(name.. ETX).
        var f = Pc410Frames.Level(1, 30m);
        byte bcc = f[^1];
        byte expected = 0;
        for (int i = 6; i < f.Length - 1; i++) expected ^= f[i];   // skip preamble(5)+STX(1); include name..ETX
        Assert.Equal(expected, bcc);
    }
}
