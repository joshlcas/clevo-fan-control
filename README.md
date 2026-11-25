# Clevo Fan Control (X170KM-G)

Replacement for the "Fan speed setting" UWP app that ships with Clevo Control Center 3.0.

## How the original app works

It never touches the EC directly. The chain is:

```
FanSpeedSetting.exe (C# UWP)
  -> InsydeDCHU.dll  (Clevo's user-mode wrapper)
    -> DeviceIoControl(0x322400) on \?\ACPI#CLV0001#1#{86994c74-ad43-4812-b7e7-0c420b5c5fd7}
      -> AcpiBridge.sys (signed Clevo kernel driver, already installed)
        -> ACPI _DSM (UUID 93f224e4-fbdc-4bbf-add6-db71bdc0afad)
          -> Embedded Controller
```

The app only offers presets (auto / max / silent / Max-Q) and a 4-point fan curve.
The underlying driver also exposes a direct per-channel duty command, which the
UWP UI never uses. That is what this tool drives.

## Commands used

| ID | Meaning |
|----|---------|
| 12 | telemetry: RPM counters, duty bytes, EC temps |
| 13 | firmware fan table, fan count, current mode |
| 14 | custom 4-point fan curve (256-byte table) |
| 104 (0x68) | **set duty** - 4 bytes, one per fan channel, 0-255 |
| 105 (0x69) | **return channels to automatic** - bitmask (0x0F = all) |
| 121 (0x79) | preset mode: byte0 = mode, byte3 = subcommand 1 |

Telemetry layout from package 12 (matches the vendor app):

```
CPU  RPM counter : [2]<<8 | [3]     CPU  duty : [16]     CPU  temp : [18]
GPU1 RPM counter : [4]<<8 | [5]     GPU1 duty : [19]     GPU1 temp : [21]
GPU2 RPM counter : [6]<<8 | [7]     GPU2 duty : [22]     GPU2 temp : [24]
```

Displayed RPM = `round(2156250 / counter)` (same formula as the vendor UI).

## Modes (command 121)

| Mode | Behaviour observed on this machine |
|------|------------------------------------|
| 0 | automatic. Sits on the 25 % floor when cool, reaches 100 % near 98 C |
| 1 | full speed, both channels 100 % |
| 6 | custom curve, follows the table written by command 14 |

Modes 3, 4 and 5 also return `rc 0` but were not fully characterised; they look
like fixed or intermediate vendor profiles rather than something the table
controls. Use 0, 1 and 6.

Note that automatic mode does **not** run the curve that package 13 reports.
Package 13 keeps returning the last written table regardless of mode, so treat
`--table` as "what the EC has stored", not "what is currently being enforced".

## Curves

Four points per channel: `T1/D1 -> T2/D2 -> T3/D3 -> 100 C/100 %`, linear between
them. `T1` and `D1` come from the EC and can not be written by command 14.

The machine shipped with:

| Channel | T1/D1 | T2/D2 | T3/D3 |
|---------|-------|-------|-------|
| CPU | 40 C / 25 % | 65 C / 41 % | 80 C / 56 % |
| GPU1 | 40 C / 25 % | 66 C / 48 % | 80 C / 59 % |

The EC does not track a written table closely: measured duty can sit well above
or below what the table predicts at the same temperature, because the firmware
smooths and second-guesses the points. In curve mode this machine would not go
below about 38 %, and that is also roughly what the shipped table itself asks
for once the cooling is not at 40 C (at 60 C it predicts 37.8 %).

The GUI therefore does not use the EC curve for control. It evaluates a curve in
software and writes the result with command 104, which is exact. `T1` is used as
the first temperature, but the duty at that point is the user's minimum duty, so
the whole range down to 0 % is available. The EC table is still written and mode
6 selected, purely as a fallback in case the process dies or the machine resumes.

### Modes in the GUI

| Mode | Behaviour |
|------|-----------|
| Automatic | firmware owns the fans (mode 0), no software control |
| Manual | one fixed duty per channel from the sliders (raw 0-255) |
| Custom | the editable curve |
| Quiet | Custom shifted 3 C later and trimmed 2 / 4 points, so it sits just under Custom |
| Smart | Custom fed a predictive temperature, see below |

### Default Custom curve

Deliberately quieter than both the firmware's automatic mode and the shipped
table, which are aggressive on this machine (automatic sat at 65 % at 68 C
during testing):

| Point | Value |
|-------|-------|
| T1 / minimum duty | 40 C / 15 % |
| T2 / D2 | 72 C / 28 % |
| T3 / D3 | 86 C / 55 % |
| 4th point | 100 C / 100 % |

So it holds roughly 25-30 % through the low 70s and ramps hard above 85 C, with
the temperature guard as the backstop. Raise D2 if you would rather trade noise
for lower temperatures. "Edit curve..." shows a live preview at 45-85 C and can
restore the shipped values.

Minimum duty is the floor at and below T1. 0 % is allowed.

### Smart mode

The same curve, evaluated at a temperature slightly ahead of reality:

```
Teff = T + clamp(trend, 0, 1.5 C/s) * 6 C + clamp((cpuBusy - 30%) / 70%, 0, 1) * 4 C
```

- `trend` is the change over a 15 s window. A heat sink takes 10-30 s to show a
  load, so the fan is normally told too late.
- `cpuBusy` is system-wide CPU utilisation from `GetSystemTimes`, smoothed with a
  short EMA. One kernel32 call per tick, no PDH counters, no WMI, no per-process
  enumeration, no allocations. This is also why the load figure leads the
  temperature: heat has to conduct to the sensor.
- Duty moves at most +12 / -6 raw steps per 2 s, and only when the target moves
  by 3 or more steps. That keeps it from hunting or whining.

Control runs every 2 s. UI polling runs every 1 s, and the fan count is cached so
the second EC read is not repeated each tick.

## Usage

GUI: double-click `FanControl.exe`. Closing the window keeps it running in the
notification area; use **Exit** there to stop it. Right-clicking the tray icon
also switches mode. "Start with Windows" writes an `HKCU` Run entry that starts it
hidden with `--tray`.

```
FanControl.exe                     open the GUI
FanControl.exe --tray              start hidden in the notification area
FanControl.exe --status            read fans once
FanControl.exe --table             read the stored fan curve
FanControl.exe --set 60            both fans to 60 %
FanControl.exe --duty 153          both fans to raw duty 153 (0-255)
FanControl.exe --cpu 60 --gpu 80   per-fan percent
FanControl.exe --max               full speed
FanControl.exe --off               duty 0
FanControl.exe --auto              back to firmware control
FanControl.exe --custom            write the saved custom curve to the EC
FanControl.exe --quiet             write the derived quiet curve to the EC
FanControl.exe --defaults          load the firmware default table (overwrites the curve)
FanControl.exe --mode 0|1|6        firmware preset (auto / max / custom)
```

`--custom` and `--quiet` on the command line only write the EC table, so the EC
interprets them loosely. The exact software curve is a GUI feature: the command
line is one-shot and has no control loop.

`--defaults` is the vendor "load default" call. It replaces the stored curve with
the firmware default, which is not the same as this machine's shipped curve.

Settings (curve, minimum duty, mode, guard, autostart) live in
`%APPDATA%\ClevoFanControl\settings.ini`. A missing or corrupt file silently
falls back to the defaults, so it can never leave the machine without a curve.

## Build

```
csc /target:winexe /optimize+ /out:FanControl.exe ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll FanControl.cs
```

`/target:winexe` matters. A console build allocates a console window before any
code runs, which is why older builds flashed a terminal next to the GUI (and, when
launched from a shell, hid the caller's terminal). The exe now has no console of
its own; the CLI path calls `AttachConsole(ATTACH_PARENT_PROCESS)` to borrow the
calling shell's, and `AllocConsole` when launched from Explorer.

## Notes

- Duty is PWM, not RPM. The EC decides how many RPM a given duty produces.
  The RPM column is feedback; to hold an exact RPM you would need a closed loop.
- `--off` / duty 0 asks the EC for 0 %. Some ECs clamp to a minimum, and the
  firmware may re-assert its curve after sleep/resume. Check the RPM readout.
- If the app is killed while a manual duty is set, the EC keeps that duty until
  something calls `--auto` (or the machine is power-cycled). The GUI's
  temperature guard only runs while the GUI is open. Exiting through the GUI (or
  a Windows shutdown) hands the fans back to automatic when "Hand fans back to
  automatic" is ticked, which is the default.
- The software curve fails safe: after three failed telemetry reads it hands the
  fans back to automatic rather than leaving a low duty behind.
- Control Center's own fan page will fight this tool if both are open. Close
  Control Center while using it.
- Keep the "Acpi Bridge" device installed. Only the UWP app is replaceable; the
  signed driver is the thing that actually reaches the EC.
