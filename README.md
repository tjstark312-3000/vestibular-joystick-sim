# VFORCE Purple GUI — NRF v1.2.2 (2026.10.29)

Windows x64 update of the purple VFORCE Vestibular Simulation Console, with
**Left Cal** and **Right Cal**, Forza Horizon 5 UDP telemetry, and Microsoft
Flight Simulator SimConnect input. This package updates the GUI application
only. It does not flash, modify or provision firmware.

## v1.2.2 game-menu hotfix

This GUI-only hotfix addresses the `ObjectDisposedException` involving
`ContextMenuStrip` observed when opening the Forza or Flight Sim game menu
on the ASUS ROG Ally. The menu lifecycle is corrected so repeated game-menu
opens and selections do not use a disposed menu. Left Cal, Right Cal, the
controller selector and the NRF v0.8.1 protocol remain part of this GUI.

The existing Windows UI smoke test now covers 24 native menu-item clicks
across the Forza, Flight Sim and controller menus, repeated opens/dismissals,
and menu disposal when the form closes. It writes `menu-lifecycle.txt` beside
the UI evidence. Passing CI establishes that tested menu path; it does not
establish an actual Ally run. A fresh on-device check of both
game menus, repeated opens and source selection is still pending for v1.2.2.
The physical controller, game telemetry and board-output checks described
below remain separate from the menu regression result.

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
including the Forza UDP loopback check. UI smoke checks
render the handheld layout without opening a serial device.

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
