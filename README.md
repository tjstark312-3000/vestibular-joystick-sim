# VFORCE — ROG Ally / XIAO USB engineering build

This branch updates the ROG Ally Windows application for the fabricated
VMocion-Minimal XIAO PCB. It retains the existing head display, joystick and game
preview, and adds **finite resistor-only electrical tests for identified nominal
firmware**. Measured calibration is not required for those fixed-code tests.
Guarded-v1 firmware remains status-only. Joystick/game/head-preview data never
starts or streams physical output in this engineering build.

The older published `v2026.08.30` executable sends legacy 9600-baud `AA ... 55`
packets and is incompatible with the new guarded firmware and V4 resistor
firmware. Use the ZIP artifact from this branch's **Windows USB compatibility
check**, rather than the old release, to test the new USB connection.

## What works in this build

- Physical ROG Ally/XInput left-stick preview, MSFS SimConnect and Forza UDP
  telemetry retain their existing input and transform code.
- Selects a unique XIAO application USB device (`VID 2886`, PID `8045` or
  `0145`). Multiple matching devices require manual COM selection. Legacy
  FTDI adapters and bootloader ports are not automatically selected.
- Opens USB CDC at 115200 with DTR asserted, then verifies actual firmware
  responses. Opening a COM port alone never marks the board verified.
- Supports `VMOCION_BENCH_100K_PAIR_V3/V4/V5` and `VMOCION_NOMINAL_2K_AB_V1`
  / `VMOCION_NOMINAL_2K_4CH_V1`
  / `VMOCION_NOMINAL_2K_4CH_1P5MA_V1`
  ASCII status and Minimal guarded-v1
  binary status, including unprovisioned receivers.
- Validates guarded CRC/nonce/header/flags and bench fixture/state fields.
  Two advancing-uptime samples are required; stale status, a reset, unexpected
  active output or disconnect invalidates the link. Active state is accepted
  only during a finite resistor test explicitly started by this application.
- Polls USB on a background thread, independently of 60 Hz visualization and
  joystick/game input. Advanced shows firmware, faults, provisioning and raw
  reported state. Commanded/modelled preview values are not measured output.
- `Resistor test` is available only for fault-free nominal profiles. It asks
  for the exact resistor-only fixture, starts a five-second zero baseline,
  waits for observed completion, and requires the operator to confirm the
  meter stayed at 0.000 V before a fixed five- or twenty-second pulse.
  Each pulse retains the firmware's existing fixed DAC codes and hardware
  deadline. A new baseline is required for each additional pulse. STOP,
  Pause, closing the test dialog, and disconnect stop the bench session.
- No arbitrary output vectors, legacy ARM/current packets, fault clears or
  calibration writes are transmitted. The app never bypasses a fault latch.
  Nominal tests use the explicit existing firmware commands; the guarded
  receiver cannot enter this path even if calibration is absent.

Installed board state is recorded separately in the firmware repository's
flash reports; this app build does not imply a flash or a measured result.
Nominal status requires the exact 2 kΩ fixture, explicit nominal-only/no-calibration
flags, and prepared/baseline/target state fields.
The four-channel profile additionally requires the two-pair fixture identifier
and channel mask 15; its proposed second resistor is not yet physically confirmed.
The higher-current profile additionally requires source_step_codes=3932. Its
distinct identity does not establish measured delivery. For that profile the
operator must confirm two separate 2 kΩ loads before its fixed-code test.
This application does not upgrade, downgrade or provision the firmware.
Its reported fault signal and GPIO state do not measure delivered current or
qualify the unspecified custom head phantom. A passive electrical head phantom
cannot establish human vestibular perception.

## Install on the ROG Ally

Download the `VFORCE-XIAO-engineering` ZIP artifact from a successful Windows
check for this branch. Extract it, keeping `VestibularJoystickSim.exe` and
`SimConnect.dll` together. Close the older app and other serial programs,
connect the PCB through a USB data cable, and open the new EXE. Advanced lets
you select a COM port when multiple boards are connected.

Expect `USB VERIFIED` and `REPORTED OFF` after valid responses. `UNVERIFIED`
means the current output state has not been confirmed. The `Resistor test`
button stays disabled for general guarded-v1 and faulted receivers. During
an explicitly started resistor test the badge shows `BENCH ACTIVE` from
firmware status, not from joystick or preview motion. No Windows-on-ROG-Ally
hardware run has been recorded; CI compilation, mocked state-machine tests,
and physical board/analog tests are separate evidence.

The unchanged older release EXE still sends `AA ... 55` legacy frames and is
not compatible with the installed guarded/nominal protocols. Replace the EXE
with this build to retain the original interface and use its new XIAO connection.
This is a finite resistor-test integration, not continuous electrode stimulation
from head tracking or game telemetry, and not a human-use release.

Optional read-only connection report, replacing COM5 with the detected port:

```powershell
Start-Process -FilePath .\VestibularJoystickSim.exe -ArgumentList '--usb-status','COM5','usb-status.txt' -Wait
Get-Content .\usb-status.txt
```

The report does not measure the analog waveform. Firmware remains output off
unless a separate qualified test session commands it.

## Build and verify

```powershell
.\build.ps1
```

Build uses the Windows .NET Framework compiler. It runs the existing packet,
transform, input-source and telemetry self-tests plus USB codec self-tests.
The Windows check additionally decodes status generated by the actual portable
firmware core under synthetic provisioning, recorded physical V4/V5 idle status,
and synthetic nominal 2 kΩ status. The fixture identifies those scopes explicitly.
The engineering ZIP includes SHA-256 sums for the EXE and SimConnect DLL.

Clinical stimulus limits, physical calibration, the phantom's measured electrical
load and hardware fault/cutoff validation remain separate requirements. No
human-use qualification or removal of electrical safeguards is provided.
