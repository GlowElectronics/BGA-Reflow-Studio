using System;
using System.Text;
using Microsoft.VisualBasic;

namespace BGAReflowStudio.Protocol;

/// <summary>
/// Pure builders for the PC410 wire frames (ANSI X3.28 / RKC polling). Each method returns the exact
/// byte sequence that is written to the serial port, with no I/O — so the framing (including the VB
/// <see cref="Conversion.Str"/> number formatting and the XOR block-check) can be unit-tested against
/// known-good captures. The logic here is a verbatim extraction of the original send code; it is not
/// "cleaned up", because the exact bytes must keep matching real hardware.
/// </summary>
public static class Pc410Frames
{
    private const byte EOT = 0x04;
    private const byte STX = 0x02;
    private const byte ETX = 0x03;
    private const byte ENQ = 0x05;

    /// <summary>Device-select preamble EOT "0011" that prefixes every message.</summary>
    private static readonly byte[] Select = { EOT, 0x30, 0x30, 0x31, 0x31 };

    /// <summary>Poll a variable: <c>EOT "0011" &lt;name&gt; ENQ</c>.</summary>
    public static byte[] Poll(string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var buf = new byte[Select.Length + nameBytes.Length + 1];
        Select.CopyTo(buf, 0);
        nameBytes.CopyTo(buf, Select.Length);
        buf[^1] = ENQ;
        return buf;
    }

    /// <summary>Ramp step (r1..r8): value sent *including* the VB leading space/sign.</summary>
    public static byte[] Ramp(int step, decimal value)
    {
        string s = VbStr(value);
        var buf = new byte[10];
        buf[0] = (byte)'r';
        buf[1] = (byte)Strings.Asc(step.ToString()[0]);
        buf[7] = ETX;
        for (int i = 1; i <= s.Length; i++) buf[i + 1] = (byte)Strings.Asc(s[i - 1]);
        buf[8] = Xor(buf, 0, 8);
        return Assemble(buf, s.Length, isRamp: true);
    }

    /// <summary>Target/level step (l1..l8): value sent *without* the leading space.</summary>
    public static byte[] Level(int step, decimal value)
        => Short((byte)'l', (byte)Strings.Asc(step.ToString()[0]), value);

    /// <summary>Dwell step (t1..t8): value sent *without* the leading space.</summary>
    public static byte[] Dwell(int step, decimal value)
        => Short((byte)'t', (byte)Strings.Asc(step.ToString()[0]), value);

    /// <summary>Holdback (Hb).</summary>
    public static byte[] Holdback(decimal value) => Short((byte)'H', (byte)'b', value);

    private static byte[] Short(byte name1, byte name2, decimal value)
    {
        string s = VbStr(value);
        var buf = new byte[8];
        buf[0] = name1;
        buf[1] = name2;
        buf[5] = ETX;
        // chars #2..#len (skip the leading sign/space), VB 1-based GetChar.
        for (int i = 2; i <= s.Length; i++) buf[i] = (byte)Strings.Asc(s[i - 1]);
        buf[6] = Xor(buf, 0, 6);
        return Assemble(buf, s.Length, isRamp: false);
    }

    private static byte[] Assemble(byte[] buf, int valueLen, bool isRamp)
    {
        int vlen = isRamp ? valueLen : valueLen - 1;   // ramp keeps the VB leading space
        int etxOff = isRamp ? 7 : 5;                   // where ETX+BCC sit in buf
        // EOT "0011" + STX + name(2) + value(vlen) + ETX + BCC, as one contiguous buffer.
        var msg = new byte[Select.Length + 1 + 2 + vlen + 2];
        int k = 0;
        Select.CopyTo(msg, k); k += Select.Length;
        msg[k++] = STX;
        msg[k++] = buf[0]; msg[k++] = buf[1];
        Array.Copy(buf, 2, msg, k, vlen); k += vlen;
        msg[k++] = buf[etxOff]; msg[k++] = buf[etxOff + 1];
        return msg;
    }

    // ---- control / status frames (fixed bodies: name + value + ETX + precomputed BCC) ----
    public static byte[] Start() => Control(new byte[] { (byte)'#', (byte)'3', 0x30, 0x30, 0x30, 0x31, ETX, 0x12 });
    public static byte[] Stop() => Control(new byte[] { (byte)'#', (byte)'3', 0x30, 0x30, 0x30, 0x30, ETX, 0x13 });
    public static byte[] Hold() => Control(new byte[] { (byte)'#', (byte)'3', 0x30, 0x30, 0x30, 0x32, ETX, 0x11 });
    public static byte[] AlarmStart() => Control(new byte[] { (byte)'A', (byte)'2', 0x30, 0x30, 0x31, ETX, (byte)'A' });
    public static byte[] AlarmStop() => Control(new byte[] { (byte)'A', (byte)'2', 0x35, 0x30, 0x30, ETX, (byte)'E' });

    /// <summary><c>EOT "0011" + STX + body</c>.</summary>
    public static byte[] Control(byte[] body)
    {
        var msg = new byte[Select.Length + 1 + body.Length];
        int k = 0;
        Select.CopyTo(msg, k); k += Select.Length;
        msg[k++] = STX;
        body.CopyTo(msg, k);
        return msg;
    }

    /// <summary>Select the active profile/channel: <c>STX "ch" &lt;hex(index)&gt; ETX BCC</c>.</summary>
    public static byte[] SelectChannel(int index)
    {
        byte hex = (byte)Strings.Asc(Conversion.Hex(index)[0]);
        var frame = new byte[] { (byte)'c', (byte)'h', hex, ETX };
        byte bcc = Xor(frame, 0, 4);
        var msg = new byte[Select.Length + 1 + 4 + 1];
        int k = 0;
        Select.CopyTo(msg, k); k += Select.Length;
        msg[k++] = STX;
        frame.CopyTo(msg, k); k += 4;
        msg[k] = bcc;
        return msg;
    }

    /// <summary>XOR block-check over buf[start .. start+count).</summary>
    public static byte Xor(byte[] buf, int start, int count)
    {
        byte x = 0;
        for (int i = start; i < start + count; i++) x ^= buf[i];
        return x;
    }

    /// <summary>VB <c>Conversion.Str</c>: leading space for non-negatives, invariant '.' separator.</summary>
    public static string VbStr(decimal value) => Conversion.Str(value);
}
