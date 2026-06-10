# Known Working Vestibular EXE

This folder keeps the exact app version that was confirmed working on June 10, 2026.

## Restore Point

- Working EXE source commit: `8a37399c1ba83fcaee2f42d4dee98f81610e9171`
- Checkpoint tag: `known-working-vestibular-2026-06-10`
- Working backup EXE: `known-good/VestibularJoystickSim_WORKING_2026-06-10.exe`
- Active EXE path: `outputs/vestibular-joystick-sim/VestibularJoystickSim.exe`
- Active EXE size: `43008` bytes
- SHA256: `3A550AAF57D035BC4C57CE2DCC889050FA21F8D7DFE9B7D0F000D05A4C013889`

## Confirmed Behavior

- Opens as `Vestibular Joystick Simulation`.
- Uses `Legacy gvs.py UART 9600`.
- COM5 is detected on the test machine.
- `Cal Left` and `Cal Right` are present.
- The user confirmed the vestibular simulation works correctly with this build.

## Restore Command

```powershell
Copy-Item -LiteralPath known-good\VestibularJoystickSim_WORKING_2026-06-10.exe -Destination outputs\vestibular-joystick-sim\VestibularJoystickSim.exe -Force
```
