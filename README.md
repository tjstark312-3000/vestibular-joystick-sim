# Vestibular Joystick Simulation

Native Windows VMocion/GVS controller prototype with a physical left-stick input path and legacy `gvs.py` serial output.

## Download

[Download the latest Windows ZIP](https://github.com/tjstark312-3000/vestibular-joystick-sim/releases/latest/download/VestibularJoystickSim-windows.zip)

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
- The `Peak mA` slider limits channel current from `0.00` to `2.50 mA`; the default startup value is `0.50 mA` for gentler first-time testing.
- Reads physical stick input from XInput, Windows joystick/HID, keyboard-style mappings, and desktop pointer movement.
- Optional `Forza physics` button blends Forza UDP physics with the controller joystick path.
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
