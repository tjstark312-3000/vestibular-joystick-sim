# VFORCE Vestibular Simulation Console

Native Windows VMocion/GVS controller prototype with a physical ROG Ally left-stick input path, game telemetry, and legacy `gvs.py` serial output.

## Download

[Download the recommended Windows ZIP](https://github.com/tjstark312-3000/vestibular-joystick-sim/releases/latest/download/VestibularJoystickSim-windows.zip) — includes the app and `SimConnect.dll`.

[Download the EXE directly](https://github.com/tjstark312-3000/vestibular-joystick-sim/releases/latest/download/VestibularJoystickSim.exe)

Extract the ZIP on the Windows device, keep `VestibularJoystickSim.exe` and `SimConnect.dll` together, then open `VestibularJoystickSim.exe`. Windows may show a SmartScreen prompt because the executable is not code-signed.

## Included

- `outputs/vestibular-joystick-sim/VestibularJoystickSim.exe` - current Windows executable.
- `outputs/vestibular-joystick-sim/SimConnect.dll` - Microsoft Flight Simulator connection library.
- `src/VestibularJoystickSim/App.cs` - source code for the main executable.
- `src/VestibularJoystickSim/VestibularJoystickSim.csproj` - project metadata for the WinForms app.
- `src/VestibularJoystickSim/app.manifest` - Per-Monitor V2 DPI and Windows compatibility metadata.
- `assets/vforce-logo.png` - embedded VFORCE wordmark used by the self-contained executable.
- `work/vestibular-exe-builder/App.cs` - mirrored WinForms source used by the legacy build layout.

## Current Behavior

- Automatically detects VMocion/DIGITUS/FTDI hardware and selects its COM port, with manual COM selection still available.
- Shows customer-facing status badges for the controller, VMocion link, and armed output state.
- Includes one-tap installed-game launch tiles for Microsoft Flight Simulator and Forza Horizon 5.
- Keeps protocol, COM, packet, and channel diagnostics in the Advanced support dialog instead of the main screen.
- Uses COM serial output at `9600` baud and never arms motion automatically.
- Sends legacy VMocion `gvs.py` UART packets: `AA len signal checksum 55`.
- Uses the `gvs.py` P/Q/R matrix for vestibular output.
- The `Peak mA` slider limits channel current from `0.00` to `2.50 mA`; the default startup value is `0.50 mA` for gentler first-time testing.
- Prioritizes the ROG Ally/XInput left stick, with the Windows joystick API as a hardware fallback. Keyboard and pointer movement cannot drive output.
- Selecting the Flight Simulator tile launches the installed game and enables SimConnect physics; it reconnects after the simulator restarts.
- Selecting the Forza tile launches Forza Horizon 5 and enables valid local race telemetry on UDP `5300` or `5607`; configure the game's Data Out IP as `127.0.0.1`.
- Only the selected game feed runs, avoiding duplicate polling; its motion can still be combined with the physical stick.
- Pause sends and holds neutral output. Disconnects, serial errors, and application close disarm output.
- Joystick visuals update at 60 Hz while the safety-critical output cadence remains at 25 ms. USB/WMI discovery runs off the UI hot path.
- Keeps the on-screen joystick out of the control path.

## Build

Run from the repository root:

```powershell
.\build.ps1
```

The build runs the executable's packet, transforms, MSFS mapping, Forza validation, and source-selection self-tests and fails if any test fails.

The build uses the .NET Framework C# compiler included with Windows:

`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`

## Safety

This is an educational/prototype hardware controller, not a medical, diagnostic, therapeutic, or clinical tool. Verify neutral output while disarmed before every session. Stop immediately if discomfort, dizziness, nausea, or unexpected output occurs.
