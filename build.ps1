$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $root 'src\VestibularJoystickSim\App.cs'
$output = Join-Path $root 'outputs\vestibular-joystick-sim\VestibularJoystickSim.exe'
$simConnectSource = Join-Path $root 'deps\SimConnect.dll'
$simConnectOutput = Join-Path $root 'outputs\vestibular-joystick-sim\SimConnect.dll'

if (-not (Test-Path -LiteralPath $csc)) {
    throw "C# compiler not found: $csc"
}

& $csc /nologo /target:winexe /optimize+ /platform:x64 /out:$output `
    /reference:System.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.Management.dll `
    $source

if (Test-Path -LiteralPath $simConnectSource) {
    Copy-Item -LiteralPath $simConnectSource -Destination $simConnectOutput -Force
}

& $output --self-test
