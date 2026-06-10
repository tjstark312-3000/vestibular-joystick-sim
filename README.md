# Vestibular Joystick Simulation

Native Windows VMocion/GVS controller prototype with a physical left-stick input path and legacy `gvs.py` serial output.

## Included

- `outputs/vestibular-joystick-sim/VestibularJoystickSim.exe` - main updated executable.
- `outputs/vestibular-joystick-sim/VestibularJoystickSim_clone.exe` - untouched clone executable kept for comparison.
- `src/VestibularJoystickSim/App.cs` - clean prebuild source code for the main executable.
- `src/VestibularJoystickSim/VestibularJoystickSim.csproj` - project metadata for the WinForms app.
- `deps/SimConnect.dll` - native SimConnect dependency used by the MSFS telemetry mode.
- `work/vestibular-exe-builder/App.cs` - WinForms source for the main executable.
- `work/vestibular-exe-builder/VestibularJoystickSim.test.exe` - local self-test build artifact.

## Current Behavior

- Uses COM serial output at `9600` baud.
- Sends legacy VMocion `gvs.py` UART packets: `AA len signal checksum 55`.
- Uses the `gvs.py` P/Q/R matrix for vestibular output.
- Reads physical stick input from XInput, Windows joystick/HID, keyboard-style mappings, and desktop pointer movement.
- Reads MSFS game physics through SimConnect when `MSFS physics + stick` is selected.
- Reads Forza Horizon 5 game physics from UDP Data Out on local ports `5300` or `5607` when `Forza Horizon 5 + stick` is selected.
- Keeps the on-screen joystick out of the control path.
- Includes Cal Left/Cal Right buttons that send opposite signed P-axis calibration pulses while Reset/Pause return the output to neutral.

## Build

Run from the repository root:

```powershell
.\build.ps1
```

The build uses the .NET Framework C# compiler included with Windows:

`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`

For Forza Horizon 5, enable Data Out in HUD/Game settings and point it at `127.0.0.1` with UDP port `5300` or `5607`. For Microsoft Flight Simulator, start the sim before selecting the MSFS input mode so SimConnect can attach.

## Safety

This is an educational/prototype hardware controller, not a medical, diagnostic, therapeutic, or clinical tool. Use neutral/reset and stop immediately if discomfort, dizziness, nausea, or unexpected output occurs.
