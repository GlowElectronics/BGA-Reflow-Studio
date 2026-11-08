#!/usr/bin/env python3
"""Generate the BGA Reflow Studio user manual as a PDF (reportlab)."""
import os, struct
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.enums import TA_LEFT, TA_CENTER
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, PageBreak,
                                Table, TableStyle, Image, ListFlowable, ListItem, HRFlowable)

OUT = os.environ.get("MANUAL_OUT", "BGA Reflow Studio - User Manual.pdf")
ICON = os.environ.get("MANUAL_ICON", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "BGAReflowStudio", "icon.ico"))
VERSION = "1.0"

INK     = colors.HexColor("#172033")
ACCENT  = colors.HexColor("#2864E8")
MUTED   = colors.HexColor("#5B6676")
LINE    = colors.HexColor("#D7DEE8")
CARDBG  = colors.HexColor("#F3F6FA")

# ---- extract a PNG frame from the .ico so the cover can show the logo ----
def extract_logo(ico_path, out_png):
    try:
        with open(ico_path, "rb") as f:
            data = f.read()
        count = struct.unpack_from("<H", data, 4)[0]
        best = None
        for i in range(count):
            o = 6 + i * 16
            w = data[o] or 256
            size = struct.unpack_from("<I", data, o + 8)[0]
            off = struct.unpack_from("<I", data, o + 12)[0]
            sig = data[off:off+4]
            is_png = sig == b"\x89PNG"
            if is_png and (best is None or w == 128 or (best[0] != 128 and w > best[0])):
                best = (w, off, size)
        if best:
            with open(out_png, "wb") as g:
                g.write(data[best[1]:best[1]+best[2]])
            return out_png
    except Exception:
        pass
    return None

SCRIPTDIR = os.path.dirname(os.path.abspath(__file__))

def png_image(fname, width_mm):
    """Embed a PNG from the script folder, scaled to width_mm keeping its aspect ratio."""
    path = os.path.join(SCRIPTDIR, fname)
    with open(path, "rb") as f:
        head = f.read(24)
    w, h = struct.unpack(">II", head[16:24])
    width = width_mm * mm
    img = Image(path, width=width, height=width * h / w)
    img.hAlign = "CENTER"
    return img

styles = getSampleStyleSheet()

def S(name, **kw):
    parent = kw.pop("parent", styles["Normal"])
    styles.add(ParagraphStyle(name, parent=parent, **kw))

S("Body", fontName="Helvetica", fontSize=10.5, leading=15, textColor=INK, spaceAfter=7)
S("H1", fontName="Helvetica-Bold", fontSize=18, leading=22, textColor=INK, spaceBefore=6, spaceAfter=10)
S("H2", fontName="Helvetica-Bold", fontSize=13, leading=17, textColor=ACCENT, spaceBefore=14, spaceAfter=5)
S("H3", fontName="Helvetica-Bold", fontSize=11, leading=15, textColor=INK, spaceBefore=9, spaceAfter=3)
S("MBullet", parent=styles["Body"], fontSize=10.5, leading=15, spaceAfter=3)
S("Note", fontName="Helvetica-Oblique", fontSize=10, leading=14, textColor=MUTED, spaceAfter=7)
S("Caption", fontName="Helvetica-Oblique", fontSize=9, leading=12, textColor=MUTED, alignment=TA_CENTER, spaceAfter=8, spaceBefore=3)
S("CoverTitle", fontName="Helvetica-Bold", fontSize=30, leading=34, textColor=INK, alignment=TA_CENTER)
S("CoverSub", fontName="Helvetica", fontSize=15, leading=20, textColor=ACCENT, alignment=TA_CENTER)
S("CoverMeta", fontName="Helvetica", fontSize=10.5, leading=16, textColor=MUTED, alignment=TA_CENTER)
S("TblCell", fontName="Helvetica", fontSize=9.5, leading=13, textColor=INK)
S("TblHead", fontName="Helvetica-Bold", fontSize=9.5, leading=13, textColor=colors.white)

def para(txt, style="Body"): return Paragraph(txt, styles[style])
def bullets(items):
    return ListFlowable([ListItem(para(t, "MBullet"), leftIndent=6, value="•") for t in items],
                        bulletType="bullet", bulletColor=ACCENT, leftIndent=14, bulletFontSize=9)

def table(rows, widths, head=True):
    data = []
    for r, row in enumerate(rows):
        data.append([Paragraph(c, styles["TblHead" if (head and r == 0) else "TblCell"]) for c in row])
    t = Table(data, colWidths=widths, repeatRows=1 if head else 0)
    ts = [("VALIGN", (0,0), (-1,-1), "MIDDLE"),
          ("LEFTPADDING", (0,0), (-1,-1), 7), ("RIGHTPADDING", (0,0), (-1,-1), 7),
          ("TOPPADDING", (0,0), (-1,-1), 5), ("BOTTOMPADDING", (0,0), (-1,-1), 5),
          ("LINEBELOW", (0,0), (-1,-1), 0.5, LINE)]
    if head:
        ts += [("BACKGROUND", (0,0), (-1,0), ACCENT),
               ("ROWBACKGROUNDS", (0,1), (-1,-1), [colors.white, CARDBG])]
    else:
        ts += [("ROWBACKGROUNDS", (0,0), (-1,-1), [colors.white, CARDBG])]
    t.setStyle(TableStyle(ts))
    return t

def hr():
    return HRFlowable(width="100%", thickness=0.6, color=LINE, spaceBefore=4, spaceAfter=8)

story = []

# ---------------- cover ----------------
story.append(Spacer(1, 40*mm))
logo = extract_logo(ICON, os.path.join(os.path.dirname(OUT) or ".", "_logo.png")) if ICON else None
if logo:
    img = Image(logo, width=26*mm, height=26*mm)
    img.hAlign = "CENTER"
    story.append(img)
    story.append(Spacer(1, 10*mm))
story.append(para("BGA Reflow Studio", "CoverTitle"))
story.append(Spacer(1, 4*mm))
story.append(para("User Manual", "CoverSub"))
story.append(Spacer(1, 16*mm))
story.append(para(f"Version {VERSION}", "CoverMeta"))
story.append(para("Reflow profiling &amp; control for ACHI IR PRO SC and ACHI IR 6500<br/>BGA rework stations (Altec PC410 controller)", "CoverMeta"))
story.append(Spacer(1, 30*mm))
story.append(para("© Glow Electronics &nbsp;·&nbsp; https://github.com/glowelectronics", "CoverMeta"))
story.append(PageBreak())

# ---------------- 1. Introduction ----------------
story.append(para("1. Introduction", "H1"))
story.append(para(
    "BGA Reflow Studio is a Windows desktop application for building, storing and running "
    "thermal reflow profiles on an ACHI BGA rework station. It communicates with the station's "
    "<b>Altec PC410</b> temperature controller over a serial (COM) link to send profile parameters, "
    "start and stop cycles, and display live temperatures.", "Body"))
story.append(para("Supported machines", "H3"))
story.append(bullets(["ACHI IR PRO SC", "ACHI IR 6500", "Other stations using the Altec PC410 controller"]))

story.append(para("Safety first", "H2"))
story.append(para(
    "This software commands real heaters that reach soldering temperatures (200&nbsp;°C and above). "
    "Always follow your station's documentation and local safety procedures, and keep an operator "
    "present during a cycle. The software provides a <b>critical-temperature auto-stop</b> and an "
    "<b>audible alarm</b>, but these are aids, not substitutes for safe operation.", "Body"))
story.append(para(
    "A locally authored profile is not a validated machine setting. Confirm temperature limits, ramp "
    "rates and interlocks against your equipment documentation and a qualified operator before use.", "Note"))

story.append(para("Disclaimer", "H2"))
story.append(para(
    "This software is provided <b>&ldquo;as is&rdquo;, and you use it entirely at your own risk.</b> "
    "<b>Glow Electronics accepts no responsibility or liability for any loss, injury, or damage</b> — to "
    "equipment, components, boards, or property — arising from the use or misuse of this software. "
    "You are responsible for knowing your machine's capabilities and safe operating limits <b>before</b> "
    "using this software with it, and for supervising every cycle.", "Note"))

# ---------------- 2. Getting started ----------------
story.append(para("2. Getting Started", "H1"))
story.append(para("Requirements", "H3"))
story.append(bullets([
    "Windows 10 or 11 (64-bit).",
    "A serial (COM) connection to the PC410 controller (USB-to-serial adapters are supported).",
    "The .NET 10 Desktop Runtime (bundled with the application package).",
]))
story.append(para("Launching", "H3"))
story.append(para(
    "Start <b>BGA Reflow Studio</b> from its shortcut or executable. Only one instance can run at a "
    "time — launching it again while it is open simply brings a notice that it is already running.", "Body"))

story.append(para("The main window at a glance", "H2"))
story.append(table([
    ["Area", "What it shows / does"],
    ["Connection bar", "Machine, Controller and Port selectors; Refresh / Connect / Settings; and the COM status and cycle-time indicators."],
    ["Status tiles", "Live board temperature (external sensor), bottom heater temperature (internal sensor), Set Point (moving set point / active-step target °C), the current Segment (step / total) and Process State, with power bars. After a run, Process State shows the last cycle's duration."],
    ["Action bar", "Start / Stop / Hold on the left; Transfer progress and the Temp.1 / Temp.2 chart toggles and Segment Beep in the centre; profile-edit and download/upload actions on the right. (Temp.2 is off by default; Segment Beep is on.)"],
    ["Thermal Profile", "The editable 8-step recipe table, Profile / Heaters / Holdback selectors, and the planned-profile chart."],
    ["Profile Library", "The profile files (.ini) found in the \\profiles folder."],
    ["Available Profile Sets", "The ten profile slots inside the selected file."],
    ["Bottom heater", "Manual power, thresholds and the Auto (PID) option."],
    ["Safety / Alarm", "Critical-temperature and sound-alarm limits (each Static or Dynamic) and alarm auto-silence."],
    ["Activity bar", "A status line at the bottom reporting the latest action."],
], [38*mm, 125*mm]))

story.append(Spacer(1, 6))
story.append(para("The main application window during a live cycle:", "Body"))
story.append(png_image("app_overview.png", 166))
story.append(para("BGA Reflow Studio — the main window while running a profile", "Caption"))

# ---------------- 3. Connecting ----------------
story.append(PageBreak())
story.append(para("3. Connecting to the Controller", "H1"))
story.append(para(
    "In the connection bar, choose your <b>Machine</b> and <b>Controller</b>, pick the serial "
    "<b>Port</b> (use <b>Refresh</b> to re-scan), then click <b>Connect</b>. Click it again to "
    "disconnect. The COM indicator on the right turns green (<b>COM OPEN</b>) when connected and "
    "shows <b>COM CLOSED</b> otherwise.", "Body"))
story.append(para(
    "Your Machine, Controller and Port choices are remembered and restored automatically the next "
    "time you open the application.", "Body"))
story.append(para(
    "To add your own machine to the list, use <b>Edit &gt; Rework Machines &gt; Add Machine…</b> and type a "
    "name. It is added to the <b>Machine</b> drop-down, selected, and saved so it reappears on the next "
    "launch. <b>Edit &gt; Rework Machines &gt; Remove Machine…</b> deletes a custom machine (the two "
    "built-in ACHI machines cannot be removed).", "Body"))
story.append(para("Serial settings", "H3"))
story.append(para(
    "Click <b>Settings</b> (or <b>File > Connection > Settings</b>) to set the port parameters. "
    "The PC410 defaults are:", "Body"))
story.append(table([
    ["Parameter", "Default"],
    ["Baud rate", "9600"],
    ["Data bits", "7"],
    ["Parity", "Even"],
    ["Stop bits", "1"],
    ["Flow control", "None"],
], [60*mm, 40*mm]))
story.append(Spacer(1, 3))
story.append(para(
    "If the port cannot be opened you will be told the machine may not be connected — check the cable, "
    "the adapter and the port settings, then re-open the port.", "Note"))

story.append(para("Matching the adapter and the application", "H3"))
story.append(para(
    "The port parameters in the application must match both the Windows driver for your USB-to-serial "
    "adapter <b>and</b> the communication settings on the PC410 controller itself. To check the driver, "
    "open <b>Device Manager &gt; Ports (COM &amp; LPT)</b>, right-click the adapter (for example "
    "<i>USB-SERIAL CH340</i>), and choose <b>Properties &gt; Port Settings</b>. Set the same values there "
    "as in the application&rsquo;s <b>Settings</b> dialog. The two dialogs below show a matching "
    "<b>9600 / 7 / Even / 1</b> configuration:", "Body"))
_imgs = Table([[png_image("com_driver.png", 60), png_image("com_app.png", 57)]],
              colWidths=[85*mm, 85*mm])
_imgs.setStyle(TableStyle([("VALIGN", (0,0), (-1,-1), "TOP"),
                           ("ALIGN", (0,0), (-1,-1), "CENTER"),
                           ("TOPPADDING", (0,0), (-1,-1), 2),
                           ("BOTTOMPADDING", (0,0), (-1,-1), 0)]))
story.append(_imgs)
_caps = Table([[para("Windows driver — Device Manager (Port Settings)", "Caption"),
                para("BGA Reflow Studio — Settings (Com Port Configuration)", "Caption")]],
              colWidths=[85*mm, 85*mm])
_caps.setStyle(TableStyle([("VALIGN", (0,0), (-1,-1), "TOP")]))
story.append(_caps)
story.append(para(
    "Only the <b>COM port number</b> changes from PC to PC — select the port your adapter is assigned "
    "(use <b>Refresh</b> to re-scan). The baud rate, data bits, parity and stop bits must be identical "
    "on both sides, and must match what the controller is configured for. A mismatch (most often the "
    "baud rate) is the usual reason a connection opens but no data is read.", "Note"))

# ---------------- 4. Profiles ----------------
story.append(para("4. Working with Profiles", "H1"))
story.append(para(
    "Profiles are organised on two levels. The <b>Profile Library</b> lists the profile files "
    "(<i>.ini</i>) kept in the application's <b>\\profiles</b> folder. Each file holds up to ten "
    "profile slots, which appear in <b>Available Profile Sets</b> once a file is selected.", "Body"))
story.append(bullets([
    "Select a file in <b>Profile Library</b> to load its profiles.",
    "Use the <b>+</b> button to create a new profile file, and the <b>-</b> button to delete the selected one.",
    "Use the <b>duplicate</b> button (the copy icon beside + and -) to copy the selected file to a new one. You are prompted for a name, and the file's custom slot names are copied across too.",
    "<b>Double-click a file</b> in Profile Library to rename it — this renames the actual .ini file on disk, and its custom slot names are carried across.",
    "Select a slot in <b>Available Profile Sets</b> (or use the <b>Profile</b> drop-down) to load that recipe into the editor.",
    "<b>Double-click a slot</b> in Available Profile Sets to give it a friendly name (see &ldquo;Renaming&rdquo; below).",
    "<b>Right-click a slot</b> for <b>Copy Set / Paste Set</b>: copy one set, then paste it onto another to overwrite that slot with the copied recipe and settings. The pasted slot's name becomes the copied name with &ldquo;-copy&rdquo; appended.",
]))

story.append(para("The 8-step recipe", "H2"))
story.append(para(
    "Each profile is a staged heating recipe of up to eight steps. Edit the table cells directly:", "Body"))
story.append(table([
    ["Column", "Meaning", "Range"],
    ["Ramp °C/s", "Heating rate toward this step's target", "-0.01 to 99.99"],
    ["Target °C", "Target temperature for the step", "0 to 300"],
    ["Dwell s", "Hold time at the target", "0 to 999"],
], [34*mm, 95*mm, 34*mm]))
story.append(Spacer(1, 3))
story.append(bullets([
    "A ramp of <b>-0.01</b> marks the <b>end</b> of the profile — remaining steps are ignored.",
    "A ramp of <b>0</b> skips that step's ramp segment.",
    "<b>Holdback (HB)</b> pauses the profile if the measured temperature runs ahead of the planned value. The recommended range is <b>5–30</b> (new profiles default to 15); a value below 5 is allowed but warns, as it can cause stepping issues. The upper limit is 30.",
]))
story.append(para(
    "<b>Step 1 tip:</b> set <b>Step 1</b>'s target higher than the current board temperature (the "
    "<b>Board Temperature</b> reading) before starting. If the first target is at or below the "
    "board's present temperature, the controller can mis-step through the segments at the start of the cycle.", "Note"))
story.append(para(
    "The <b>planned-profile chart</b> below the table previews the time/temperature curve calculated "
    "from your targets, ramp rates and dwell times. During a cycle, live traces for Temperature.1 and "
    "Temperature.2 are drawn on the same chart (toggle them with the Temp.1 / Temp.2 check boxes in the "
    "action bar). Use <b>Pop out</b> (top-right of the Thermal Profile card) to open the chart in a "
    "separate window that keeps updating live alongside the embedded one.", "Body"))
story.append(Spacer(1, 4))
story.append(png_image("live_chart.png", 166))
story.append(para("The pop-out live chart during a cycle: planned curve (red), the controller set point (orange steps), "
                  "and the live board temperature (green)", "Caption"))

story.append(para("Renaming a profile set", "H2"))
story.append(para(
    "Double-click any entry in <b>Available Profile Sets</b> and type a new name. Custom names are stored "
    "<b>locally</b> (not written into the .ini file) and are tied to the file, so they reappear whenever "
    "that file is loaded. A rename is treated as an unsaved change — click <b>Save</b> to keep it.", "Body"))

story.append(para("Saving changes", "H2"))
story.append(para(
    "Saving is <b>not</b> automatic. The <b>Save</b> button persists your edits (steps, holdback, "
    "bottom-heater settings, safety values and pending renames) into the selected profile file.", "Body"))
story.append(bullets([
    "<b>Grey</b> Save button — there are no unsaved changes.",
    "<b>Green</b> Save button — you have unsaved changes.",
    "After saving, the button returns to grey.",
    "If you exit with unsaved changes, a dialog offers <b>Save &amp; exit</b>, <b>Don't save</b> or <b>Cancel</b>.",
]))

story.append(para("Importing, saving-as and exporting", "H2"))
story.append(bullets([
    "<b>Edit > Profile > Load Profile…</b> — add an external .ini file to the library and select it.",
    "<b>Edit > Profile > Save Profile As…</b> — save the current set of ten profiles under a new file name.",
    "<b>Export CSV</b> — export the current 8-step recipe as a CSV file for records or spreadsheets. The file is named after the profile file and set (for example <i>Default_Xbox_One.csv</i>).",
]))

# ---------------- 5. Bottom heater ----------------
story.append(PageBreak())
story.append(para("5. Bottom Heater &amp; Power", "H1"))
story.append(para(
    "The bottom (lower) heater pre-heats the board from underneath. Bringing the whole board up to a "
    "soak temperature reduces thermal shock and warping, and lets the top heater reach reflow without "
    "over-driving one spot. The <b>Bottom Heater</b> card controls how much bottom power is applied and "
    "how it is scheduled — manually in stages, or automatically with a PID loop.", "Body"))

story.append(para("Heater modes", "H2"))
story.append(para(
    "The <b>Heaters</b> selector (in the Thermal Profile card) chooses which heaters run during a cycle:", "Body"))
story.append(table([
    ["Mode", "Behaviour"],
    ["Together", "Both heaters run. The top heater follows the profile; the bottom heater follows the manual stages or the PID loop below."],
    ["Top", "Only the top heater runs the profile; the bottom heater is not driven."],
    ["Bottom", "Only the bottom heater is driven (manual power / PID); the top profile is not started."],
], [30*mm, 133*mm]))
story.append(para(
    "Bottom-heater control (manual stages and Auto PID) is active in <b>Together</b> and <b>Bottom</b> modes.", "Note"))

story.append(para("Manual power", "H2"))
story.append(para(
    "<b>Power bottom</b> sets the bottom-heater power level from 10% to 100%. The selected percentage is "
    "delivered to the bottom output as a proportional on/off duty cycle across each control cycle "
    "(for example 60% means the output is on roughly six-tenths of the time); 100% keeps it continuously on. "
    "The live <b>Power Bottom</b> bar and the <b>Bottom Heater Temperature</b> tile show the current output "
    "and temperature. The <b>Board Temperature</b> tile reads the external board-temperature "
    "sensor (Temp.1); the <b>Bottom Heater Temperature</b> tile reads the station's internal sensor (Temp.2).", "Body"))

story.append(para("When the bottom sensor isn't fitted", "H3"))
story.append(para(
    "Not every machine has the bottom (internal) sensor wired. When it isn't, the reading is "
    "meaningless — usually well over 1000&nbsp;°C — so the <b>Bottom Heater Temperature</b> tile shows "
    "<b>No Data</b> and the bottom power is held at <b>0%</b> (the bottom output is not driven). For this "
    "reason the <b>Temp.2</b> chart trace is <b>off by default</b>; tick <b>Temp.2</b> in the action bar "
    "only on machines where the bottom sensor is fitted.", "Body"))

story.append(para("Temperature-staged power", "H3"))
story.append(para(
    "Instead of a single fixed level, you can have the bottom power step up automatically as the board warms. "
    "Four stages — <b>40%, 60%, 80% and 100%</b> — each have a temperature <b>threshold</b> (0–500&nbsp;°C) and "
    "an enable check box. As the measured temperature rises through the bands you define, the enabled stage "
    "for that band sets the bottom power:", "Body"))
story.append(table([
    ["Band (measured temperature)", "Bottom power", "Applied when"],
    ["At or below the 40% threshold", "40%", "the 40% box is ticked"],
    ["Between the 40% and 60% thresholds", "60%", "the 60% box is ticked"],
    ["Between the 60% and 80% thresholds", "80%", "the 80% box is ticked"],
    ["Between the 80% and 100% thresholds", "100%", "the 100% box is ticked"],
], [74*mm, 28*mm, 61*mm]))
story.append(bullets([
    "Set the four thresholds in <b>ascending</b> order (for example 30, 70, 120, 160&nbsp;°C).",
    "A stage whose box is <b>unticked</b> is skipped, so you can shape the bottom-heat curve to your board.",
    "Staging is a manual-mode feature; it is disabled while Auto (PID) is on.",
]))

story.append(para("Auto (PID) mode", "H2"))
story.append(para(
    "Tick <b>Auto (PID)</b> to let the software regulate the bottom heater automatically instead of using "
    "fixed stages. A PID controller (gains Kp&nbsp;0.85, Ki&nbsp;4.67, Kd&nbsp;0.15) drives the bottom power "
    "to hold the <b>bottom temperature (Temp.2)</b> at the controller <b>set point</b> plus the "
    "<b>Auto boost</b> offset, adjusting continuously as the process runs.", "Body"))
story.append(bullets([
    "<b>Auto boost (set point +)</b> raises the bottom-heater target a set number of degrees above the "
    "controller set point — increase it for more bottom heat / faster bottom warm-up.",
    "While Auto is on, the manual threshold fields and check boxes are disabled and the heater mode is forced "
    "to <b>Together</b>.",
]))
story.append(para(
    "Bottom heating adds energy to the board — keep the thresholds, boost and limits within your board's and "
    "station's ratings. The critical-temperature auto-stop (Section&nbsp;6) still applies to the top temperature.", "Note"))

# ---------------- 6. Running a cycle ----------------
story.append(para("6. Running a Cycle", "H1"))
story.append(para(
    "With the port open and a profile loaded, use the action-bar buttons:", "Body"))
story.append(table([
    ["Button", "Action"],
    ["Start", "Selects the stored profile slot on the controller and starts the cycle (it does not upload your latest edits — see below)."],
    ["Stop", "Stops the cycle and switches the bottom heater output off."],
    ["Hold", "Pauses / resumes the running cycle."],
    ["Hot Update", "Sends the most recently edited parameter to the controller live, during a cycle."],
    ["Compare &amp; Start", "Reads every parameter back from the controller and compares it with the PC profile; offers to upload any differences before starting."],
    ["Download 1 Set / Upload 1 Set", "Download the current profile set from, or upload it to, the controller."],
    ["Download All / Upload All", "Download or upload all ten profile sets."],
], [38*mm, 125*mm]))
story.append(Spacer(1, 4))
story.append(para(
    "<b>Downloading</b> from the controller asks whether to put the downloaded data in a <b>new</b> profile "
    "file (named after the machine) or overwrite the <b>current</b> file, so you never overwrite a saved file "
    "by accident. <b>Uploading</b> to the controller asks you to confirm first, since it overwrites the "
    "profile sets stored on the machine.", "Note"))
story.append(Spacer(1, 4))
story.append(para(
    "While running, the status tiles show the live Top (Temp.1) and Bottom (Temp.2) temperatures, the "
    "Set Point and current segment, the process state and the top/bottom power bars. The cycle timer "
    "appears in the connection bar, and when the cycle ends the Process State tile reports the last "
    "cycle's duration (for example <i>Last cycle 08:02</i>). Tick <b>Segment Beep</b> (in the action bar) "
    "to play a short beep each time the profile advances to the next step.", "Body"))

story.append(para("What controls the cycle", "H2"))
story.append(para(
    "Once you press <b>Start</b>, the <b>PC410 controller runs the heating profile itself</b>. The software "
    "uploads the recipe to a profile slot in the controller, selects that slot and issues the run command; "
    "from then on the controller executes the eight ramp/soak steps from its own memory, with its own "
    "timing and PID, and advances through the segments on its own. The current segment is shown beneath the "
    "Set Point tile.", "Body"))
story.append(para("During a cycle the software supervises and monitors:", "Body"))
story.append(bullets([
    "<b>Monitors</b> — polls and displays the top/bottom temperatures, power, set point, segment and run status, and plots the live traces against the planned curve.",
    "<b>Commands</b> — Start, Stop and Hold, plus Hot Update to push a single edited value to the controller live.",
    "<b>Safety</b> — the critical-temperature auto-stop, the sound alarm, and the automatic stop when the controller reports the profile has finished.",
    "<b>Controls the bottom heater</b> — the bottom heater is <b>not</b> part of the controller's stored profile; the software drives it in real time (manual staged power or the Auto PID loop).",
]))
story.append(para(
    "Because <b>Start</b> runs whatever is already stored in the controller's slot, editing the recipe on "
    "the PC does <b>not</b> change a cycle until you upload it with <b>Upload 1 Set</b> / <b>Upload All</b>. If the "
    "PC and the controller may have drifted apart, use <b>Compare &amp; Start</b> — it reads every value "
    "back, flags any differences and offers to upload them before starting.", "Note"))

story.append(para("Safety &amp; alarm thresholds", "H2"))
story.append(para(
    "In the <b>Safety / Alarm</b> card, the <b>Critical temp</b> (auto-stop) and <b>Sound alarm temp</b> each "
    "have a mode selector — <b>Static</b> or <b>Dynamic</b>:", "Body"))
story.append(bullets([
    "<b>Static</b> — you enter an absolute temperature in °C, used exactly as typed regardless of the profile. Defaults: Critical <b>230&nbsp;°C</b>, Sound alarm <b>225&nbsp;°C</b>.",
    "<b>Dynamic</b> — you enter a small number (1–15) that is <b>added to the highest target temperature in the current profile set</b>. Defaults: Critical <b>+15</b>, Sound alarm <b>+10</b>.",
]))
story.append(para(
    "For example, with a set whose highest target is 200&nbsp;°C, a Dynamic Critical of +15 and alarm of +10 "
    "give effective limits of 215&nbsp;°C and 210&nbsp;°C. Dynamic limits follow the recipe, so they adjust "
    "automatically when you change the profile. <b>Alarm auto-silence (ticks)</b> is unaffected by the mode.", "Body"))

story.append(para("Safety behaviours", "H2"))
story.append(bullets([
    "<b>Critical-temperature auto-stop</b> — if Temp.1 reaches the effective <b>Critical temp</b>, the cycle is stopped and a warning is shown.",
    "<b>End-of-profile auto-stop</b> — when the controller reports the profile has finished, the software immediately stops the cycle (switching the bottom heater off) and then shows a <i>Profile Complete</i> message.",
    "<b>Set-point-zero auto-stop</b> — when the controller's set point returns to 0 °C (the set has concluded, but the machine would otherwise keep running), the software stops the cycle automatically about 5 seconds later.",
    "<b>Example-profile warning</b> — starting a profile that still holds the built-in example (placeholder) recipe prompts you to confirm first, since it is not tuned for your board.",
    "<b>Sound alarm</b> — when Temp.1 reaches the effective <b>Sound alarm temp</b>, alarm.wav plays; it auto-silences after the configured number of ticks.",
    "<b>Communication watchdog</b> — if valid traffic stops for several seconds, the port is closed and you are notified.",
]))

# ---------------- 7. Menu reference ----------------
story.append(PageBreak())
story.append(para("7. Menu Reference", "H1"))
story.append(table([
    ["Menu", "Item", "Description"],
    ["File", "Connection > Connect / Disconnect", "Open or close the serial port."],
    ["File", "Connection > Settings", "Edit the serial port parameters."],
    ["File", "Serial Terminal…", "Open a live view of the TX/RX serial traffic between the app and the controller (hex)."],
    ["File", "Local Database > Export Local Database…", "Save your custom profile-set names to a file (not a profile export)."],
    ["File", "Local Database > Import Local Database…", "Load custom profile-set names from a file, replacing the current ones (not a profile import)."],
    ["File", "Local Database > Clean Database…", "Delete all locally stored custom profile names. The profile .ini files are not affected."],
    ["File", "Exit", "Close the application (prompts if there are unsaved changes)."],
    ["Edit", "Profile > Load Profile…", "Import an external .ini into the library."],
    ["Edit", "Profile > Save Profile As…", "Save the ten profiles under a new file."],
    ["Edit", "Rework Machines > Add Machine…", "Add a custom machine name to the Machine list (saved for next time)."],
    ["Edit", "Rework Machines > Remove Machine…", "Remove a custom machine. The two built-in ACHI machines cannot be removed."],
    ["Edit", "Screenshot…", "Save a PNG image of the window."],
    ["Help", "Manuals", "Open this user manual (and any other PDFs in the \\manuals folder), or Open Folder."],
    ["Help", "Support", "Support the project — this software is free; you can buy the author a coffee on Ko-fi."],
    ["Help", "About", "Version, copyright and the Glow Electronics link."],
], [20*mm, 60*mm, 83*mm]))

# ---------------- 8. Data & files ----------------
story.append(PageBreak())
story.append(para("8. Data &amp; Files", "H1"))
story.append(table([
    ["Location", "Contents"],
    ["\\profiles\\*.ini (beside the app)", "Your profile files — each holds ten profiles. These appear in the Profile Library."],
    ["settings.ini (beside the app)", "Serial port and settings, machine/controller choice, custom machines, chart visibility and the active profile file."],
    ["\\manuals (beside the app)", "PDF manuals, listed under Help > Manuals."],
    ["alarm.wav (beside the app)", "The sound played for the temperature alarm."],
    ["serial.log (beside the app)", "A serial traffic log (TX/RX, rewritten each connection) for diagnosing communication problems."],
    ["%LOCALAPPDATA%\\BGA Reflow Studio", "Local database of custom profile-set names. Managed from File > Local Database (export / import / clean)."],
    ["achi-reflow.log (beside the app)", "Error log, created only if a problem occurs."],
], [72*mm, 91*mm]))
story.append(Spacer(1, 4))
story.append(para(
    "<b>File &gt; Local Database &gt; Clean Database</b> permanently deletes the local name database — your custom profile-set names will be "
    "lost, and names revert to those stored in the .ini files. The profile recipes themselves are not affected.", "Note"))

# ---------------- 9. Troubleshooting ----------------
story.append(para("9. Troubleshooting", "H1"))
story.append(table([
    ["Symptom", "What to check"],
    ["No ports listed", "Confirm the USB-to-serial adapter/driver is installed, then click Refresh."],
    ["Port will not open", "Another program may be using it; check the port name and that the adapter is connected."],
    ["&ldquo;Communication error / machine not connected&rdquo;", "Verify cable, adapter and serial settings (9600 / 7 / Even / 1) — they must match the controller — then re-open the port."],
    ["Connects but no temperature / data", "Almost always a baud-rate mismatch: set the same baud on the adapter driver, the app and the controller (default 9600). Open File > Serial Terminal to watch the live traffic — TX lines but no RX means the controller isn't replying."],
    ["Bottom heater shows &ldquo;No Data&rdquo;", "The bottom thermocouple isn't fitted/wired; its reading is ignored and bottom power held at 0. Normal on machines without a bottom sensor."],
    ["Edits don't take effect on the machine", "Start runs the controller's stored slot. Upload your edits with Upload 1 Set / Upload All, or use Compare &amp; Start."],
    ["Values look wrong after Compare", "Use Compare &amp; Start and choose to upload the PC values to the controller."],
    ["Custom names disappeared", "They are removed by File > Local Database > Clean Database; re-enter them, or restore from an exported local database."],
], [70*mm, 93*mm]))

# ---------------- 10. Appendix ----------------
story.append(para("Appendix A — About the PC410 link", "H1"))
story.append(para(
    "BGA Reflow Studio speaks the PC410 controller's serial protocol (an ANSI&nbsp;X3.28 / RKC-style "
    "polling scheme). Parameter values are formatted to match the controller exactly. Because the link "
    "commands real heater hardware, do not modify cabling or port settings while a cycle is running, and "
    "always verify behaviour against your equipment documentation.", "Body"))

story.append(Spacer(1, 10))
story.append(hr())
story.append(para(f"BGA Reflow Studio — User Manual · Version {VERSION} · © Glow Electronics", "CoverMeta"))

# ---- page numbers / footer ----
def footer(canvas, doc):
    canvas.saveState()
    canvas.setStrokeColor(LINE); canvas.setLineWidth(0.5)
    canvas.line(18*mm, 14*mm, A4[0]-18*mm, 14*mm)
    canvas.setFont("Helvetica", 8); canvas.setFillColor(MUTED)
    canvas.drawString(18*mm, 9*mm, "BGA Reflow Studio — User Manual")
    canvas.drawRightString(A4[0]-18*mm, 9*mm, f"Page {doc.page}")
    canvas.restoreState()

def cover(canvas, doc):  # no footer on the cover
    pass

doc = SimpleDocTemplate(OUT, pagesize=A4,
                        leftMargin=18*mm, rightMargin=18*mm, topMargin=18*mm, bottomMargin=20*mm,
                        title="BGA Reflow Studio - User Manual", author="Glow Electronics")
doc.build(story, onFirstPage=cover, onLaterPages=footer)
print("Wrote", OUT)
