$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $root 'src\VestibularJoystickSim\App.cs'
$usbSource = Join-Path $root 'src\VestibularJoystickSim\VmocionUsb.cs'
$icon = Join-Path $root 'src\VestibularJoystickSim\VMocion.ico'
$manifest = Join-Path $root 'src\VestibularJoystickSim\app.manifest'
$vforceLogo = Join-Path $root 'assets\vforce-logo.png'
$msfsGameIcon = Join-Path $root 'assets\msfs-game-icon.png'
$forzaGameIcon = Join-Path $root 'assets\forza-game-icon.png'
$output = Join-Path $root 'outputs\vestibular-joystick-sim\VestibularJoystickSim.exe'
$simConnectSource = Join-Path $root 'deps\SimConnect.dll'
$simConnectOutput = Join-Path $root 'outputs\vestibular-joystick-sim\SimConnect.dll'
$outputDirectory = Split-Path -Parent $output

if (-not (Test-Path -LiteralPath $csc)) {
    throw "C# compiler not found: $csc"
}

if (-not (Test-Path -LiteralPath $icon)) {
    throw "Application icon not found: $icon"
}

if (-not (Test-Path -LiteralPath $manifest)) {
    throw "Application manifest not found: $manifest"
}

if (-not (Test-Path -LiteralPath $vforceLogo)) {
    throw "VFORCE logo not found: $vforceLogo"
}

if (-not (Test-Path -LiteralPath $msfsGameIcon)) {
    throw "Microsoft Flight Simulator icon not found: $msfsGameIcon"
}

if (-not (Test-Path -LiteralPath $forzaGameIcon)) {
    throw "Forza icon not found: $forzaGameIcon"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $csc /nologo /target:winexe /optimize+ /platform:x64 /out:$output "/win32icon:$icon" "/win32manifest:$manifest" "/resource:$vforceLogo,VestibularJoystickSim.VForceLogo.png" "/resource:$msfsGameIcon,VestibularJoystickSim.MsfsGameIcon.png" "/resource:$forzaGameIcon,VestibularJoystickSim.ForzaGameIcon.png" `
    /reference:System.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.Management.dll `
    $source $usbSource

if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $simConnectSource) {
    Copy-Item -LiteralPath $simConnectSource -Destination $simConnectOutput -Force
}

$selfTest = Start-Process -FilePath $output -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden
if ($selfTest.ExitCode -ne 0) {
    throw "VestibularJoystickSim self-test failed with exit code $($selfTest.ExitCode)."
}
