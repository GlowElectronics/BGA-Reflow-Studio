# BGA Reflow Studio

A **.NET 10 WPF** desktop app for **ACHI IR PRO SC** and **ACHI IR 6500** BGA
rework stations. It edits thermal reflow profiles, runs reflow cycles, shows
live top/bottom temperatures, and transfers parameters to and from the station's
**Altec PC410** temperature controller over a serial link. Charting uses
ScottPlot.WPF; the UI logic reuses the UI-agnostic `Models/` and `Protocol/` layers.

## Features

- 10 thermal profiles × 8 steps (ramp rate, target setpoint, dwell time) + holdback.
- Live visualization: reference reflow curve + Temperature.1 / Temperature.2 / Set Point / current segment (ScottPlot).
- Run / Stop / Hold, with the start handshake (channel select + read-back).
- Parameter transfer to/from the controller: Single From/To, All From/To, and Compare-before-Start.
- Manual bottom-heater power staging (temperature thresholds + 40/60/80/100 % stages) and **Auto PID** mode (Kp 0.85 / Ki 4.67 / Kd 0.15).
- Critical-temperature auto-stop, audible alarm (`alarm.wav`) with auto-silence, and a communication watchdog.
- Hotkeys: `H` hold, `Space` stop, `PageUp`/`PageDown` single to/from, `F1..F10` select profile, `Esc` exit.

## PC410 serial protocol

ANSI X3.28 / RKC-style polling. Number→ASCII formatting uses the in-box
`Microsoft.VisualBasic` runtime for exact byte fidelity on the wire. Default link:
**19200 baud, 7 data bits, Even parity, 1 stop bit** (parity is forced Even on open).

- Device select: `EOT "0011"`
- Read a variable: `… <name> ENQ`
- Write a variable: `… STX <name> <value> ETX <XOR-BCC>`
- Response: `STX <name> <value[5]> ETX <XOR-BCC>`, `ACK`=0x06, `NAK`=0x15

## Files the app uses (next to the executable)

| File | Purpose |
|------|---------|
| `settings.ini` | serial port, chart visibility, active profiles file |
| `profiles.ini` | the 10 thermal profiles (`[0]..[9]`) |
| `alarm.wav` | optional sound played for the temperature alarm |
| `achi-reflow.log` | error log (created on demand) |

Without a `profiles.ini` the app seeds safe default profiles on first run.

## Build & run

```bash
dotnet build
dotnet run
```

## Project layout

| Path | Purpose |
|------|---------|
| `Protocol/Pc410Controller.cs` | Serial framing, command encoding, response parsing |
| `Models/ThermalProfile.cs` | Profile model + INI load/save |
| `Models/IniFile.cs` | Managed INI reader/writer |
| `Models/ReflowCurve.cs` | Reference reflow-curve builder |
| `Models/PidRegulator.cs` | Auto-mode bottom-heater PID |
| `App.xaml` / `App.xaml.cs` | WPF entry point + flat modern theme (styles, palette) |
| `MainWindow.xaml` / `.xaml.cs` | Main window UI + control logic (timers, transfers) |
| `SerialSettingsWindow.xaml` / `.xaml.cs` | Serial-port configuration dialog |
