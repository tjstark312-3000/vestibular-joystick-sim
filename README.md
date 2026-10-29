# VFORCE Purple GUI — NRF v1.2.5 (2026.10.29)

Windows x64 update of the purple VFORCE Vestibular Simulation Console, with
**Left Cal** and **Right Cal**, Forza Horizon 5 UDP telemetry, and Microsoft
Flight Simulator SimConnect input. This package updates the GUI application
only. It does not flash, modify or provision firmware.

## v1.2.5 mapping selected from the Packet Definition PDF

The user selected the **Representative Values to Outputs Mapping Table** on
pages 4-5 of `Vmocion Hardware Technical Document - Packet Definition.pdf`
as the direction-mapping authority. Reference SHA-256:
`26f9be766b207fcc2ac6cfdf7782939f2d342760d6fc8a5aceac02c9c4f7e997`.
This replaces the December PRD/Python matrix used in v1.2.4.

Application command conventions are **+P = right roll**, **+Q = front pitch**,
and **+R = right yaw**, in channel A/B/C/D (DAC 1/2/3/4) order:

```text
A =      Q + R
B = -P - Q - R
C =      Q - R
D =  P - Q + R
```

The six pure-axis endpoints match the PDF table:

```text
                A   B   C   D
Right roll      0  -1   0  +1
Left roll       0  +1   0  -1
Front pitch    +1  -1  +1  -1
Back pitch     -1  +1  -1  +1
Right yaw      +1  -1  -1  +1
Left yaw       -1  +1  +1  -1
```

Each full-scale pure axis reaches the selected nominal command limit. The
PRD's 1.5x pitch weighting has been removed. Between the representative
endpoints, the GUI uses linear superposition with a single shared peak
limiter; this interpolation policy is an implementation choice, not an
additional mixed-axis table supplied by the PDF. The existing default
**0.50 mA**, hard ceiling **1.50 mA**, balanced downward byte quantization,
neutral, watchdog and explicit-rearm behavior are retained.

**Left Cal now requests negative P; Right Cal requests positive P.** Thus
left roll leaves A/C neutral with B positive and D negative, and right roll
reverses B/D. Their five-second timing and Stop Cal/Pause disarm are retained.

The PDF diagram places A at the left temple, B behind the left ear, C at the
right temple and D behind the right ear. **This is the selected reference
order, not a continuity test of the physical cable.** Its A/B placement
differs from the previous PRD/Python convention; check the actual harness
against the selected drawing before any electrical bench test.

Forza and MSFS native rate signs are preserved from v1.2.4. The application
direction convention above has software tests, but native positive rates
have not been correlated with actual right-roll/front-pitch/right-yaw game
maneuvers on the Ally. Physical game direction, joystick movement, USB receipt
and measured output remain separate live acceptance checks.

The PDF also contains a different `00 ... FF` float32 voltage protocol and
conflicting baud-rate statements. **This release adopts its direction table
only.** It does not send those voltage commands or adopt its voltage values.
The unchanged NRF v0.8.1 Classic receiver accepts the GUI's already-mixed
A/B/C/D bytes and does not apply the firmware ASCII `AXES` matrix again.
Consequently no firmware change or flash is needed for this GUI path. The
firmware's optional ASCII `AXES` mode retains its older PRD matrix and is not
used by this release; it must not be substituted for the selected GUI path.

## Retained v1.2.4 capture listener recovery

This GUI-only update corrects a capture failure exposed by the external
synthetic Forza UDP test. Discarding a socket backlog can temporarily close
the telemetry listeners; capture must allow the normal listener restart
instead of reporting that transient reset as a permanent port-bind failure.
A real failure to bind both Forza ports still reports an error.

The aligned-head frame assumption and no-USB capture mode introduced in
v1.2.3 are retained. Use the complete v1.2.5 package for the current mapping
and capture fix.
Synthetic test packets are clearly labeled as fixtures and do not establish
telemetry from a running game or measured board output.

## Aligned-head assumption and telemetry capture

Game telemetry already supplies body-frame rotation rates: **P = roll**,
**Q = pitch**, and **R = yaw**. This update keeps those rates in their supplied
frame when preparing the four-channel command. The animated head's display
angles no longer rotate the body rates a second time. That extra rotation
could mix axes even though no head orientation had been measured.

This build has no physical head-tracker input. Its **aligned-head fallback**
assumes the head frame is aligned with the simulator's body frame. The head
animation is a display preview, not a measured head pose or an additional
input to the command matrix.

The host applies the selected PDF direction matrix and balanced byte
quantization in this aligned frame. These are host-model amplitudes;
delivered current is not measured and actual head movement is not tracked.

The game-menu lifecycle fix from v1.2.2 is retained. Windows UI smoke checks
exercise 24 native menu-item clicks, repeated opens/dismissals and disposal
when the form closes; `menu-lifecycle.txt` records that regression check.
Passing these checks does not establish live game physics or board output.

## Install on a Windows Steam Deck or ROG Ally

1. Extract the complete ZIP into a folder. Keep `VestibularJoystickSim.exe`
   and `SimConnect.dll` together; do not run the EXE from inside the ZIP.
2. Use Windows x64 with .NET Framework 4.8. This is a Windows executable;
   a native SteamOS build is not included. The handheld UI is checked at
   1280 × 800.
3. Close other VMocion apps and serial monitors, connect the NRF board with a
   USB data cable, and wait at least three seconds after board startup.
4. Open the EXE. Select the board's application COM port and click **Connect**.
   Use **Scan** and manual COM selection if automatic discovery does not find
   the correct port. A bootloader COM port is not the application port.
5. Select the input feed and use **Arm** explicitly when the setup is ready.
   Output starts disarmed. Use **Disarm** before changing the setup.

The connection indicator confirms the host COM connection. This firmware's
Classic protocol does not acknowledge output or report measured current, so
the GUI cannot verify that the board is armed or that analog output occurred.
Advanced diagnostics show the selected port and transmitted command state.

## ASUS ROG Ally left-stick setup and on-device check

In the Ally's Command Center, set **Control Mode** explicitly to **Gamepad**
and enable **Embedded Controller**. Avoid Desktop mode or automatic switching
between Gamepad and Desktop modes as focus changes. This GUI reads the left
stick; moving the right stick is not the left-stick verification.

Use **Controller** in the left-stick card to choose **Auto (active)** or a
specific **XInput 1–4** slot. Auto selects an active controller; selecting a
specific slot helps when an external or virtual controller is also connected.
Changing the controller selection disarms output and requires an explicit Arm.

Before connecting or arming physical output on Tyler's Ally:

1. Open the updated GUI and leave it disarmed. Check that the controller
   indication identifies the embedded left-stick input.
2. Move the left stick left, right, up and down, then through the diagonals.
   Check that the on-screen stick follows the correct direction and returns
   to center when released. Change the Controller selection if another device
   is being shown.
3. With Forza or Flight Simulator running, repeat the preview check with the
   GUI focused and then with the game focused. Confirm the intended input
   remains available when the GUI runs in the background.
4. After confirming the intended input and focus behavior, continue with the
   normal device workflow. Changing Controller selection requires an explicit
   Arm afterward.

Windows CI checks native controller API binding and synthetic raw-axis cases.
Those checks do not verify the embedded controller, mode configuration, driver
behavior, foreground/background delivery, or physical stick movement on an
ASUS ROG Ally. Tyler's on-device check is still required for those results.

## Left Cal and Right Cal

The two buttons run finite, opposite-direction check ramps using the selected
command scale. They use the existing four-channel motion protocol, override
game/stick motion while the five-second ramp runs, and disarm when complete.
Click the active direction again or **Stop Cal** to stop and disarm.
They are directional checks: they do not measure electrical output, save
calibration coefficients, or change firmware calibration.

The nominal command slider spans **0–1.50 mA**, starting at **0.50 mA**. The
label is a firmware nominal command, not a measured-current indication. The
NRF profile maps that nominal range to the accepted balanced **28–228**
channel-byte range and keeps neutral at 128.

The GUI stops its command session on input expiry or a serial failure and
requires a fresh explicit **Arm**. New input never automatically rearms a
stopped session. **Pause**, **Disarm**, and closing the app interrupt the
direction check. Restart the check explicitly after stopping.

## Forza Horizon 5

In Forza's telemetry settings, enable **Data Out**, set the destination IP to
`127.0.0.1`, and choose UDP port **5300** or **5607**. Run Forza on the same
Windows device as this GUI and begin driving. Select the **Forza 5** feed in
the GUI and check that it reports live motion before arming.

The input reads the standard Forza Sled fields present in supported Data Out
packets, including angular velocity and attitude. It rejects invalid/nonfinite
data, ignores non-loopback senders, and expires stale telemetry. A valid
race-stopped packet clears live motion immediately.

Each game tile opens a menu: choose telemetry from an already running game,
launch the Steam edition, or launch the Microsoft Store edition. The Flight
Sim menu includes Steam launch choices for 2020 and 2024. Selecting a game
disarms output; Arm again explicitly when ready. If a launcher is unavailable,
start the game manually and keep the feed selected.
Another telemetry application can occupy the UDP port; choose the other
supported port or close the conflicting application.

## Microsoft Flight Simulator

Keep the included x64 `SimConnect.dll` beside the EXE. Start Microsoft Flight
Simulator locally and load into a flight, then select **Flight Sim** in the
GUI. The app requests aircraft body rotation rates through SimConnect and
reports whether the connection and motion feed are live. Paused or stopped
simulator state suppresses game motion. If the game tile
does not open the installed edition, launch Flight Simulator manually.

This package preserves the local SimConnect integration. Simulator edition,
installation, game launch and end-to-end board operation still require a
check on the target Windows device; packaged DLL presence is not proof of
a successful simulator connection.

## Telemetry capture without opening USB

The `--capture-telemetry` command records either the Forza or Flight Simulator
feed to CSV for inspection. It does not open a COM/USB transport, arm output,
or send device packets. The current test setup has no connected NRF board.
Capture previews use the default **0.50 mA nominal limit**, recorded in the
summary; they do not inherit another GUI window's slider setting.

```powershell
$capture = Start-Process -FilePath '.\VestibularJoystickSim.exe' `
  -ArgumentList '--capture-telemetry forza --seconds 30 --out forza-capture.csv' `
  -Wait -PassThru
$capture.ExitCode
```

Replace `forza` with `msfs` to capture the Flight Simulator feed, using a
separate output filename. Run the relevant game locally and enter active
driving or flight for live motion samples. A stopped, paused or unavailable
game can yield no fresh motion. Rows with `fresh=false` can retain the last
raw sample; inspect `latest_sample_age_ms` and the freshness flag before using
those values. A capture process completing is not proof
that useful game telemetry arrived. A normal quiet capture exits with code 0.
If another GUI or telemetry process prevents the Forza ports from binding,
the capture exits with code 4 and records the error in its summary.

Capture rows describe received telemetry and the **PREVIEW** command derived
from it. Matrix values and channel bytes in a capture are modeled host
commands, not actual transmissions, measured currents or delivered waveforms.
Capture diagnostics distinguish preview data from serial port/packet counts;
USB-open and transmitted-packet counts must remain zero in this mode. The
companion `<output.csv>.summary.json` records the selected mapping and
application axis convention, flags physical game signs as unverified, and
records total/fresh rows,
`usb_open_count`, `actual_packets_sent`, `serial_connected`, `armed`, and any
capture error. A successful non-actuating capture reports both counts as 0,
with `serial_connected=false` and `armed=false`.

## Firmware compatibility

This GUI targets the existing **vmocion-classic-firmware v0.8.1** release,
identity `classic_r8_1_distribution`, for XIAO nRF52840 Sense. Its protocol is:

- USB CDC at **9600 baud, 8 data bits, no parity, 1 stop bit**, with DTR set.
- Packet framing `AA length payload checksum 55`; checksum is the payload
  sum modulo 256.
- Four-channel payload `0A A B C D`; neutral is `128` on every channel.
- Every channel is constrained to **28–228**, matching the firmware's
  accepted Classic command range.
- Explicit arm sends neutral and Reset (`01`), waits for host transmission
  and a 100 ms reset-processing interval, then Direct/Arm (`02`) and exact
  neutral. The app sends commands every 25 ms from a separate serial worker.
- Input snapshots expire after 100 ms. A known sender gap of 250 ms stops
  the host session; the firmware retains its independent command watchdog.
- Disarm and shutdown send neutral followed by Reset. Serial/input faults
  require operator rearming.

The older purple 2.50 mA encoding can produce channel bytes outside this
firmware's accepted range. Use this complete NRF GUI package with this
firmware instead of mixing EXEs from earlier builds. No 115200-baud status
probe is sent during the Classic session.

## Verification and scope

`SHA256SUMS.txt` records hashes for the packaged files. `BUILD-INFO.txt`
records the exact source commit and Windows workflow run. The Windows build
runs packet, motion-transform, telemetry parser, calibration and deterministic
serial sender tests, controller API binding and synthetic left-stick cases,
including the Forza UDP loopback check. UI smoke checks render the handheld
layout without opening a serial device. All six PDF directions are checked
at default and maximum nominal limits, including exact neutral A/C channels
for roll. GUI command-path tests check signed game/joystick vectors at varied
display attitudes, plus Left Cal/Right Cal through mock transport.
A separate two-second quiet Forza
capture check verifies CSV/summary consistency, zero fresh game samples and
zero USB opens/transmitted packets; it does not stand in for live driving.
The same Windows build also runs fresh synthetic UDP fixtures against its
newly built EXE, checking the PDF matrix, full packet checksum, source/run/hash
provenance and no-USB invariants before creating the package.

These checks establish source/build behavior and simulated transport behavior.
They do not establish installation on Tyler's board, sustained Windows USB
delivery, measured electrical output, or game-to-board behavior on a Steam
Deck. The target firmware release reports `human_use=0`; this package does
not change that qualification.

## Build from source

From a Windows PowerShell prompt in the repository root:

```powershell
.\build.ps1
```

The script uses the Windows .NET Framework x64 compiler, embeds the existing
purple interface assets, copies SimConnect, and runs device-free self-tests.
The `Purple NRF Windows build` workflow produces the ZIP and UI evidence.
