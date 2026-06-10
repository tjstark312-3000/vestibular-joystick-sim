# Vestibular Joystick Simulation

Native Windows VMocion/GVS controller prototype with a physical left-stick input path and legacy `gvs.py` serial output.

## Included

- `outputs/vestibular-joystick-sim/VestibularJoystickSim.exe` - restored first-uploaded executable.
- `outputs/vestibular-joystick-sim/VestibularJoystickSim_clone.exe` - untouched clone executable kept for comparison.
- `known-good/VestibularJoystickSim_WORKING_2026-06-10.exe` - confirmed working restore copy.
- `src/VestibularJoystickSim/App.cs` - clean prebuild source code for the main executable.
- `src/VestibularJoystickSim/VestibularJoystickSim.csproj` - project metadata for the WinForms app.
- `work/vestibular-exe-builder/App.cs` - WinForms source for the main executable.
- `work/vestibular-exe-builder/VestibularJoystickSim.test.exe` - local self-test build artifact.

## Current Behavior

- Uses COM serial output at `9600` baud.
- Sends legacy VMocion `gvs.py` UART packets: `AA len signal checksum 55`.
- Uses the `gvs.py` P/Q/R matrix for vestibular output.
- Reads physical stick input from XInput, Windows joystick/HID, keyboard-style mappings, and desktop pointer movement.
- Optional `MSFS physics` checkbox blends Microsoft Flight Simulator SimConnect physics with the physical left stick when the simulator is running.
- Keeps the on-screen joystick out of the control path.

## Build

Run from the repository root:

```powershell
.\build.ps1
```

The build uses the .NET Framework C# compiler included with Windows:

`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`

## Safety

This is an educational/prototype hardware controller, not a medical, diagnostic, therapeutic, or clinical tool. Use neutral/reset and stop immediately if discomfort, dizziness, nausea, or unexpected output occurs.
