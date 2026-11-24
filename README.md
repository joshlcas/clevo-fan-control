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
them. `T1` and `D1` come from the EC and are not written by command 14.

Original - the curve this machine shipped with:

| Channel | T1/D1 | T2/D2 | T3/D3 |
|---------|-------|-------|-------|
| CPU | 40 C / 25 % | 65 C / 41 % | 80 C / 56 % |
| GPU1 | 40 C / 25 % | 66 C / 48 % | 80 C / 59 % |

Quiet - both mid points moved 3 C later and trimmed 2 / 4 points, so on paper it
sits just under the original everywhere below 100 C:

| Channel | T1/D1 | T2/D2 | T3/D3 |
|---------|-------|-------|-------|
| CPU | 40 C / 25 % | 68 C / 39 % | 83 C / 52 % |
| GPU1 | 40 C / 25 % | 69 C / 46 % | 83 C / 55 % |

The table is written, the EC accepts it (`rc 14`) and selects mode 6, but this EC
does not track the curve closely: measured duty can sit well above or below what
the table predicts at the same temperature, because the firmware smooths and
second-guesses the points. If you want a specific fan speed, set the duty
directly - that path is exact.

## Usage

GUI: double-click `FanControl.exe` (or the Start Menu shortcut).

```
FanControl.exe --status              read fans once
FanControl.exe --table               read the stored fan curve
FanControl.exe --set 60              both fans to 60 %
FanControl.exe --duty 153            both fans to raw duty 153 (0-255)
FanControl.exe --cpu 60 --gpu 80     per-fan percent
FanControl.exe --max                 full speed
FanControl.exe --off                 duty 0
FanControl.exe --auto                back to firmware control
FanControl.exe --quiet               write the quiet curve and select it
FanControl.exe --original            write back the curve this machine shipped with
FanControl.exe --defaults            load the firmware default table (overwrites the curve)
FanControl.exe --mode 0|1|6          firmware preset (auto / max / custom)
```

`--defaults` is the vendor "load default" call. It replaces the stored curve with
the firmware default, which is not the same as this machine's shipped curve - use
`--original` to get back to the curve in the table above.

## Notes

- Duty is PWM, not RPM. The EC decides how many RPM a given duty produces.
  The RPM column is feedback; to hold an exact RPM you would need a closed loop.
- `--off` / duty 0 asks the EC for 0 %. Some ECs clamp to a minimum, and the
  firmware may re-assert its curve after sleep/resume. Check the RPM readout.
- If the app is killed while a manual duty is set, the EC keeps that duty until
  something calls `--auto` (or the machine is power-cycled). The GUI's
  temperature guard only runs while the GUI is open.
- Control Center's own fan page will fight this tool if both are open. Close
  Control Center while using it.
- Keep the "Acpi Bridge" device installed. Only the UWP app is replaceable; the
  signed driver is the thing that actually reaches the EC.
