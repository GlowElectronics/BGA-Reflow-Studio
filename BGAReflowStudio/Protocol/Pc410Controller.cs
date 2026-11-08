using System.IO;
using System.IO.Ports;
using System.Text;
using Microsoft.VisualBasic;

namespace BGAReflowStudio.Protocol;

/// <summary>
/// Serial driver for the Altec PC410 temperature controller used in the ACHI IR PRO SC
/// and IR 6500 rework stations. Framing is ANSI X3.28 / RKC-style polling: a device-select
/// preamble <c>EOT "0011"</c>, reads as <c>name + ENQ</c>, writes as <c>STX name value ETX BCC</c>,
/// and 10-byte responses <c>STX name value[5] ETX XOR-BCC</c>. Number→ASCII formatting
/// uses the VB runtime (<see cref="Conversion.Str"/>) for exact byte fidelity — this matters
/// because the frames command real heater hardware.
/// </summary>
public sealed class Pc410Controller : IDisposable
{
    private const byte EOT = 0x04;
    private const byte STX = 0x02;
    private const byte ETX = 0x03;
    private const byte ENQ = 0x05;
    public const byte ACK = 0x06;
    public const byte NAK = 0x15;

    private readonly SerialPort _port = new();
    private readonly object _writeLock = new();
    private readonly SynchronizationContext? _sync;
    private Thread? _reader;
    private volatile bool _reading;

    /// <summary>Raised for a valid STX record: (symbol, 5-char value, parsed value).</summary>
    public event Action<string, string, float>? ResponseReceived;
    public event Action? AckReceived;
    public event Action? NakReceived;
    public event Action<Exception>? PortError;
    /// <summary>Raised (on the UI context) for every logged TX/RX line — feeds the Serial Terminal.</summary>
    public event Action<string>? TrafficLogged;

    public Pc410Controller() => _sync = SynchronizationContext.Current;

    public bool IsOpen => _port.IsOpen;
    public string PortName => _port.PortName;

    public void Open(string portName, int baudRate, int dataBits, int stopBits, bool dtr = true, bool rts = true)
    {
        if (_port.IsOpen) return;
        _port.PortName = portName;
        _port.BaudRate = baudRate;
        _port.Parity = Parity.Even;          // original forces Even on open
        _port.DataBits = dataBits;
        _port.StopBits = stopBits switch { 2 => StopBits.Two, _ => StopBits.One };
        _port.ReadTimeout = 500;
        _port.WriteTimeout = 500;
        // .NET Core's System.IO.Ports de-asserts DTR/RTS on open, unlike the old .NET
        // Framework app which left the driver defaults (asserted). Many RS-232 devices
        // won't drive their TX line without these high, which looks like a dead port and
        // trips the comms watchdog ~10 s after connecting. Assert by default.
        _port.DtrEnable = dtr;
        _port.RtsEnable = rts;
        _port.Open();
        try { _port.DiscardInBuffer(); } catch { /* ignore */ }
        // Use a dedicated blocking-read thread rather than the DataReceived event: on .NET
        // Core / .NET 5+ that event is unreliable with some USB-serial drivers (it may never
        // fire even though bytes are arriving), which looks exactly like a silent device.
        _reading = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Pc410Reader" };
        _reader.Start();
        if (LogPath != null)
        {
            try { File.WriteAllText(LogPath, ""); } catch { /* truncate: a fresh log per connection */ }
        }
        if (Tracing)
        {
            string lines;
            try { lines = $"CTS={_port.CtsHolding} DSR={_port.DsrHolding} CD={_port.CDHolding}"; }
            catch { lines = "CTS/DSR/CD=?"; }
            Log($"open {portName} {baudRate} {dataBits}E{(stopBits == 2 ? 2 : 1)} DTR={dtr} RTS={rts} {lines}");
        }
    }

    public void Close()
    {
        _reading = false;
        try { if (_port.IsOpen) { _port.DiscardInBuffer(); _port.Close(); } } catch { /* ignore */ }
        try { _reader?.Join(1000); } catch { /* ignore */ }
        _reader = null;
    }

    // ---- serial traffic logging (diagnostics) -----------------------------------------

    /// <summary>When set, every TX/RX byte is appended here as hex, for diagnosing dead links.</summary>
    public string? LogPath { get; set; }
    private readonly object _logLock = new();

    /// <summary>Whether to build/emit trace lines at all (file log on, or the Serial Terminal listening).</summary>
    private bool Tracing => LogPath != null || TrafficLogged != null;

    private void Log(string line)
    {
        var ts = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        if (LogPath is string p)
        {
            try { lock (_logLock) File.AppendAllText(p, ts + Environment.NewLine); }
            catch { /* ignore */ }
        }
        if (TrafficLogged != null) Raise(() => TrafficLogged?.Invoke(ts));
    }

    private static string Hex(byte[] buf, int start, int count)
    {
        var sb = new StringBuilder(count * 3);
        for (int i = start; i < start + count; i++) sb.Append(buf[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Write a whole message as ONE contiguous <c>_port.Write</c>. The original ran on a native
    /// UART where splitting the select/name/ENQ into separate writes was harmless; on a USB-serial
    /// adapter each write incurs the adapter's latency timer (FTDI defaults to 16 ms), inserting
    /// inter-character gaps that make the RKC controller abort the poll and never reply. Sending
    /// the bytes in one buffer keeps them back-to-back on the wire.
    /// </summary>
    private void Send(byte[] buf, int count)
    {
        lock (_writeLock)
        {
            if (!_port.IsOpen) return;
            if (Tracing) Log("TX " + Hex(buf, 0, count));
            _port.Write(buf, 0, count);
        }
    }

    // ---- reads (polls) ----------------------------------------------------------------

    /// <summary>Poll a variable: <c>EOT "0011" &lt;name&gt; ENQ</c> (sent as one contiguous write).</summary>
    public void Poll(string name)
    {
        if (!_port.IsOpen) return;
        var frame = Pc410Frames.Poll(name);
        Send(frame, frame.Length);
    }

    public void PollTop() => Poll("PV");
    public void PollBottom() => Poll("P2");
    public void PollPowerTop() => Poll("OP");
    public void PollSegment() => Poll("SE");
    public void PollSetPoint() => Poll("SP");
    public void PollRunStatus() => Poll("#3");
    public void PollAlarm2() => Poll("A2");

    // ---- parameter writes (selects) ---------------------------------------------------

    /// <summary>Ramp step (r1..r8): value sent *including* the VB leading space.</summary>
    public void WriteRamp(int step, decimal value) => SendFrame(Pc410Frames.Ramp(step, value));

    /// <summary>Target/level step (l1..l8): value sent *without* the leading space.</summary>
    public void WriteLevel(int step, decimal value) => SendFrame(Pc410Frames.Level(step, value));

    /// <summary>Dwell step (t1..t8): value sent *without* the leading space.</summary>
    public void WriteDwell(int step, decimal value) => SendFrame(Pc410Frames.Dwell(step, value));

    /// <summary>Holdback (Hb).</summary>
    public void WriteHoldback(decimal value) => SendFrame(Pc410Frames.Holdback(value));

    private void SendFrame(byte[] frame)
    {
        if (!_port.IsOpen) return;
        Send(frame, frame.Length);
    }

    // ---- control / status commands -----------------------------------------------------

    public enum HeaterMode { Together = 0, Top = 1, Bottom = 2 }

    /// <summary>Start: <c>STX "#3" "0001" ETX 0x12</c>. Only valid for Together/Top (per original).</summary>
    public void Start(HeaterMode mode)
    {
        if (mode is HeaterMode.Together or HeaterMode.Top) SendFrame(Pc410Frames.Start());
    }

    /// <summary>Stop: <c>STX "#3" "0000" ETX 0x13</c>.</summary>
    public void Stop() => SendFrame(Pc410Frames.Stop());

    /// <summary>Hold: <c>STX "#3" "0002" ETX 0x11</c>.</summary>
    public void Hold() => SendFrame(Pc410Frames.Hold());

    /// <summary>Bottom output / buzzer ON: <c>STX "A2" "001" ETX 'A'</c>.</summary>
    public void AlarmStart() => SendFrame(Pc410Frames.AlarmStart());

    /// <summary>Bottom output / buzzer OFF: <c>STX "A2" "500" ETX 'E'</c>.</summary>
    public void AlarmStop() => SendFrame(Pc410Frames.AlarmStop());

    /// <summary>Select the active profile/channel: <c>STX "ch" &lt;hex(index)&gt; ETX BCC</c> then read it back.</summary>
    public void SelectChannel(int index) => SendFrame(Pc410Frames.SelectChannel(index));

    /// <summary>Read-back poll used during the start handshake: <c>EOT "0011" "ch" ENQ</c>.</summary>
    public void PollChannel() => Poll("ch");

    // ---- receive -----------------------------------------------------------------------

    private void ReadLoop()
    {
        if (Tracing) Log("reader started");
        while (_reading)
        {
            try
            {
                if (!_port.IsOpen) break;
                int first = _port.ReadByte();   // blocks up to ReadTimeout (500 ms)
                switch (first)
                {
                    case STX:
                        ParseRecord();
                        break;
                    case ACK:
                        if (Tracing) Log("RX 06 ACK");
                        Raise(() => AckReceived?.Invoke());
                        break;
                    case NAK:
                        if (Tracing) Log("RX 15 NAK");
                        Raise(() => NakReceived?.Invoke());
                        break;
                    default:
                        if (Tracing) Log($"RX {first:X2} ?");
                        break;
                }
            }
            catch (TimeoutException)
            {
                // No byte within ReadTimeout — normal between polls; keep listening.
                // (The comms watchdog in the UI handles a genuinely dead link.)
            }
            catch (Exception ex)
            {
                if (_reading) Raise(() => PortError?.Invoke(ex));
                break;
            }
        }
        if (Tracing) Log("reader stopped");
    }

    private void ParseRecord()
    {
        byte vname1 = (byte)_port.ReadByte();
        byte vname2 = (byte)_port.ReadByte();
        byte v1 = (byte)_port.ReadByte();
        byte v2 = (byte)_port.ReadByte();
        byte v3 = (byte)_port.ReadByte();
        byte v4 = (byte)_port.ReadByte();
        byte v5 = (byte)_port.ReadByte();
        byte etx = (byte)_port.ReadByte();
        byte crc = (byte)_port.ReadByte();

        byte crcCompare = (byte)(vname1 ^ vname2 ^ v1 ^ v2 ^ v3 ^ v4 ^ v5 ^ etx);
        if (Tracing)
        {
            var frame = new[] { STX, vname1, vname2, v1, v2, v3, v4, v5, etx, crc };
            Log($"RX {Hex(frame, 0, 10)}  '{(char)vname1}{(char)vname2}' crc={(crc == crcCompare ? "OK" : "BAD")}");
        }
        if (v3 == (byte)'.') v3 = (byte)','; // normalise decimal to pl-PL comma, as original

        string symbol = $"{(char)vname1}{(char)vname2}";
        string value = $"{(char)v1}{(char)v2}{(char)v3}{(char)v4}{(char)v5}";
        if (crc != crcCompare) return;

        float dec = (float)Conversion.Val(value);
        Raise(() => ResponseReceived?.Invoke(symbol, value, dec));
    }

    // ---- helpers -----------------------------------------------------------------------

    private void Raise(Action action)
    {
        if (_sync != null) _sync.Post(_ => action(), null);
        else action();
    }

    public void Dispose()
    {
        _reading = false;
        try { _reader?.Join(500); } catch { /* ignore */ }
        _port.Dispose();
    }
}
