param(
    [Parameter(Mandatory = $true)][string] $ExecutablePath,
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string] $ExpectedExeSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string] $ExpectedSourceCommit,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+$')][string] $ExpectedBuildRunId
)

$ErrorActionPreference = 'Stop'
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$actualHash = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $ExpectedExeSha256) { throw 'Synthetic capture must use the designated v1.2.7 EXE.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath).FileVersion.Trim() -ne '1.2.7.0') {
    throw 'PDF direction-table validation requires application version 1.2.7.0.'
}
$buildInfo = @(Get-Content -LiteralPath (Join-Path (Split-Path $ExecutablePath -Parent) 'BUILD-INFO.txt'))
if ($buildInfo -notcontains "source_commit=$ExpectedSourceCommit" -or
    $buildInfo -notcontains "workflow_run=$ExpectedBuildRunId" -or
    $buildInfo -notcontains 'firmware_modified=false') {
    throw 'Designated executable build provenance does not match.'
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$csv = Join-Path $OutputDirectory 'SYNTHETIC-forza.csv'
$scope = 'SYNTHETIC UDP FIXTURE ONLY. No running game, no game/controller input injection, no USB transport or device packets. This does not establish live game telemetry or physical output.'
$scope | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'SYNTHETIC-NOT-LIVE-GAME-DATA.txt')

function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Number([string] $Text) {
    return [double]::Parse($Text, [Globalization.CultureInfo]::InvariantCulture)
}

function CloseEnough([double] $Actual, [double] $Expected) {
    return [Math]::Abs($Actual - $Expected) -lt 0.00001
}

function Put-Int32([byte[]] $Packet, [int] $Offset, [int] $Value) {
    [BitConverter]::GetBytes($Value).CopyTo($Packet, $Offset)
}

function Put-Single([byte[]] $Packet, [int] $Offset, [single] $Value) {
    [BitConverter]::GetBytes($Value).CopyTo($Packet, $Offset)
}

# Independent packet-definition PDF oracle: A=Q+R, B=-P-Q-R, C=Q-R, D=P-Q+R.
# Baseline maxRate (3.14/4), shared peak limiter, nominal limit0.5; pure0.3 ->12 steps.
$fixtures = @(
    @{ name = 'right-roll-positive'; x = 0.0; y = 0.0; z = 0.3; p = 0.3; q = 0.0; r = 0.0; bytes = @(128,116,128,140) },
    @{ name = 'front-pitch-positive'; x = 0.3; y = 0.0; z = 0.0; p = 0.0; q = 0.3; r = 0.0; bytes = @(140,116,140,116) },
    @{ name = 'right-yaw-positive'; x = 0.0; y = 0.3; z = 0.0; p = 0.0; q = 0.0; r = 0.3; bytes = @(140,116,116,140) }
)
$sent = [Collections.Generic.List[object]]::new()
$sender = [Net.Sockets.UdpClient]::new()
$target = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 5300)
$capture = $null
$clock = [Diagnostics.Stopwatch]::StartNew()
$sequence = 0
$timestamp = 100000

try {
    $arguments = '--capture-telemetry forza --seconds 5 --out "{0}"' -f $csv
    $capture = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -PassThru
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(2)
    while (-not (Test-Path -LiteralPath $csv)) {
        if ($capture.HasExited) { throw 'Capture exited before creating its CSV.' }
        if ([DateTime]::UtcNow -gt $readyDeadline) { throw 'Capture CSV was not created within two seconds.' }
        Start-Sleep -Milliseconds 10
    }
    # CSV creation precedes the first poll/bind. Repeated datagrams below also
    # tolerate an initial packet being sent before the listener is ready.
    Start-Sleep -Milliseconds 100
    $clock.Restart()

    foreach ($fixture in $fixtures) {
        $phaseStart = $clock.ElapsedMilliseconds
        $nextSend = $phaseStart
        for ($tick = 0; $tick -lt 32; $tick++) {
            while ($clock.ElapsedMilliseconds -lt $nextSend) { Start-Sleep -Milliseconds 1 }
            $packet = [byte[]]::new(232)
            Put-Int32 $packet 0 1
            Put-Int32 $packet 4 $timestamp
            Put-Single $packet 44 $fixture.x
            Put-Single $packet 48 $fixture.y
            Put-Single $packet 52 $fixture.z
            # Nonzero attitude is deliberately independent of pure body rates.
            Put-Single $packet 56 0.65
            Put-Single $packet 60 -0.4
            Put-Single $packet 64 0.25
            [void] $sender.Send($packet, $packet.Length, $target)
            $sent.Add([pscustomobject]@{ fixture = $fixture.name; native_timestamp_ms = $timestamp; sender_elapsed_ms = $clock.ElapsedMilliseconds; race_on = 1 })
            $sequence++
            $timestamp += 25
            $nextSend = $phaseStart + ($tick + 1) * 25
        }
        while ($clock.ElapsedMilliseconds -lt $phaseStart + 800) { Start-Sleep -Milliseconds 1 }
    }

    $stopStart = $clock.ElapsedMilliseconds
    for ($tick = 0; $tick -lt 12; $tick++) {
        while ($clock.ElapsedMilliseconds -lt $stopStart + $tick * 25) { Start-Sleep -Milliseconds 1 }
        $packet = [byte[]]::new(232)
        Put-Int32 $packet 0 0
        Put-Int32 $packet 4 $timestamp
        [void] $sender.Send($packet, $packet.Length, $target)
        $sent.Add([pscustomobject]@{ fixture = 'race-stopped'; native_timestamp_ms = $timestamp; sender_elapsed_ms = $clock.ElapsedMilliseconds; race_on = 0 })
        $sequence++
        $timestamp += 25
    }

    if (-not $capture.WaitForExit(10000)) { throw 'Five-second capture did not finish.' }
    Require ($capture.ExitCode -eq 0) "Capture exited with code $($capture.ExitCode)."
}
finally {
    $sender.Dispose()
    if ($null -ne $capture -and -not $capture.HasExited) { $capture.Kill() }
    $sent | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'SYNTHETIC-sender-fixtures.csv') -NoTypeInformation
}

$summary = Get-Content -LiteralPath "$csv.summary.json" -Raw | ConvertFrom-Json
$rows = @(Import-Csv -LiteralPath $csv)
Require ($summary.scope -eq 'shadow_telemetry_preview_no_usb' -and $summary.source -eq 'forza') 'Incorrect capture mode.'
Require ($summary.rows -eq $rows.Count -and $summary.fresh_rows -gt 0) 'Fresh capture rows are missing or count mismatches.'
Require ($summary.usb_open_count -eq 0 -and $summary.actual_packets_sent -eq 0 -and
    $summary.serial_connected -eq $false -and $summary.armed -eq $false -and $null -eq $summary.error) 'Capture violated no-USB/no-transmission invariants.'
Require ($summary.head_frame -eq 'aligned_body_head_assumption_no_tracking') 'Head-frame assumption changed.'
Require ($summary.matrix_reference -eq 'packet_definition_pdf_pages_4_5') 'Capture metadata does not identify the selected PDF table.'
Require ($summary.command_axis_convention -eq 'P_positive_right_roll_Q_positive_front_pitch_R_positive_right_yaw') 'Capture metadata axis convention differs from the PDF fixture.'
Require ($summary.physical_game_axis_signs_verified -eq $false) 'Capture metadata must keep physical game direction verification unconfirmed.'
Require (CloseEnough ([double] $summary.nominal_limit_mA) 0.5) 'Unexpected nominal preview limit.'
Require (CloseEnough ([double] $summary.matrix_gain_mA_per_rad_s) (0.5 / (3.14 / 4.0))) 'PDF matrix gain must use baseline maxRate without PRD pitch weighting.'

$fresh = @($rows | Where-Object fresh -eq 'true')
Require ($fresh.Count -eq $summary.fresh_rows) 'Fresh row count mismatches summary.'
$fixtureResults = [Collections.Generic.List[object]]::new()
$nonneutral = 0

foreach ($row in $rows) {
    Require ($row.actual_packets_sent -eq '0' -and $row.serial_connected -eq 'false' -and $row.armed -eq 'false') 'A row reports active serial output.'
    $bytes = @('a','b','c','d' | ForEach-Object { [int] $row.('preview_byte_' + $_) })
    $p = Number $row.slew_preview_p_rad_s
    $q = Number $row.slew_preview_q_rad_s
    $r = Number $row.slew_preview_r_rad_s
    $raw = @(($q + $r), (-$p - $q - $r), ($q - $r), ($p - $q + $r))
    $peak = ($raw | ForEach-Object { [Math]::Abs($_) } | Measure-Object -Maximum).Maximum
    $gain = 0.5 / [Math]::Max((3.14 / 4.0), $peak)
    $channel = 0
    foreach ($letter in @('a','b','c','d')) {
        Require (CloseEnough (Number $row.('nominal_preview_' + $letter + '_mA')) ($raw[$channel] * $gain)) 'Nominal preview differs from the independent PDF direction-table matrix.'
        $channel++
    }
    Require (($bytes | Measure-Object -Sum).Sum -eq 512) 'Preview channel bytes are not balanced around neutral.'
    Require (@($bytes | Where-Object { $_ -lt 28 -or $_ -gt 228 }).Count -eq 0) 'Preview byte is outside NRF Classic range.'
    if (@($bytes | Where-Object { $_ -ne 128 }).Count -gt 0) { $nonneutral++ }
    $packet = @($row.preview_packet_hex.Split('-') | ForEach-Object { [Convert]::ToInt32($_, 16) })
    Require ($packet.Count -eq 9 -and $packet[0] -eq 170 -and $packet[1] -eq 5 -and $packet[2] -eq 10 -and $packet[8] -eq 85) 'Invalid preview framing.'
    $checksum = ($packet[2] + $packet[3] + $packet[4] + $packet[5] + $packet[6]) -band 255
    Require ($packet[7] -eq $checksum) 'Preview checksum is incorrect.'
    for ($i = 0; $i -lt 4; $i++) { Require ($packet[$i + 3] -eq $bytes[$i]) 'Preview packet disagrees with channel bytes.' }
}
Require ($nonneutral -gt 0) 'No nonneutral synthetic command preview was produced.'

foreach ($fixture in $fixtures) {
    $matching = @($fresh | Where-Object {
        (CloseEnough (Number $_.native_x_rad_s) $fixture.x) -and
        (CloseEnough (Number $_.native_y_rad_s) $fixture.y) -and
        (CloseEnough (Number $_.native_z_rad_s) $fixture.z)
    })
    Require ($matching.Count -ge 3) "Missing fresh samples for $($fixture.name)."
    $plateau = 0
    foreach ($row in $matching) {
        Require ((CloseEnough (Number $row.body_p_rad_s) $fixture.p) -and
            (CloseEnough (Number $row.body_q_rad_s) $fixture.q) -and
            (CloseEnough (Number $row.body_r_rad_s) $fixture.r)) 'Native-to-body P/Q/R mapping differs.'
        Require ((CloseEnough (Number $row.aligned_command_p_rad_s) $fixture.p) -and
            (CloseEnough (Number $row.aligned_command_q_rad_s) $fixture.q) -and
            (CloseEnough (Number $row.aligned_command_r_rad_s) $fixture.r)) 'Aligned commands differ from body rates.'
        $prefix = [byte[]] @($row.raw_prefix_hex.Split('-') | ForEach-Object { [Convert]::ToByte($_, 16) })
        Require ($prefix.Length -eq 68) 'Native source prefix is missing.'
        Require ([BitConverter]::ToInt32($prefix, 0) -eq 1) 'A fresh native source packet is not race-active.'
        Require ([BitConverter]::ToUInt32($prefix, 4) -eq [uint32] $row.native_timestamp_ms) 'Native timestamp differs from raw packet.'
        Require ((CloseEnough ([BitConverter]::ToSingle($prefix, 44)) $fixture.x) -and
            (CloseEnough ([BitConverter]::ToSingle($prefix, 48)) $fixture.y) -and
            (CloseEnough ([BitConverter]::ToSingle($prefix, 52)) $fixture.z)) 'Native source prefix differs from fixture angular rates.'
        Require ((CloseEnough ([BitConverter]::ToSingle($prefix, 56)) 0.65) -and
            (CloseEnough ([BitConverter]::ToSingle($prefix, 60)) -0.4) -and
            (CloseEnough ([BitConverter]::ToSingle($prefix, 64)) 0.25)) 'Synthetic nonzero attitude was not captured.'
        $actual = @('a','b','c','d' | ForEach-Object { [int] $row.('preview_byte_' + $_) })
        if (($actual -join ',') -eq ($fixture.bytes -join ',')) { $plateau++ }
    }
    Require ($plateau -ge 3) "Expected signed pure-axis preview was not reached for $($fixture.name)."
    $fixtureResults.Add([pscustomobject]@{ fixture = $fixture.name; fresh_rows = $matching.Count; plateau_rows = $plateau; expected_preview_bytes = $fixture.bytes })
}

$stopped = @($rows | Where-Object { $_.fresh -eq 'false' -and $_.source -eq 'Forza race not active' })
Require ($stopped.Count -gt 0) 'IsRaceOn=0 was not observed as stopped telemetry.'
$last = $rows[-1]
Require ($last.fresh -eq 'false' -and $last.preview_packet_hex -eq 'AA-05-0A-80-80-80-80-0A-55') 'Capture did not return to neutral after race stop.'

$validation = [ordered]@{
    scope = $scope
    result = 'PASS'
    tested_executable_sha256 = $actualHash
    tested_source_commit = $ExpectedSourceCommit
    original_package_workflow_run = [long] $ExpectedBuildRunId
    mapping_reference = 'Packet-definition PDF direction table; P=right roll, Q=front pitch, R=right yaw in the application command convention'
    physical_game_maneuver_signs_verified = $false
    independent_matrix = @(@(0,1,1), @(-1,-1,-1), @(0,1,-1), @(1,-1,1))
    normalization_baseline = 'maxRate; shared max(maxRate, peak(raw)) denominator'
    capture_seconds = 5
    sender_datagrams = $sequence
    timestamp_increment_ms = 25
    fixture_attitude_radians = @{ yaw = 0.65; pitch = -0.4; roll = 0.25 }
    fresh_rows = $fresh.Count
    nonneutral_preview_rows = $nonneutral
    race_stopped_rows = $stopped.Count
    fixture_results = $fixtureResults
    preview_checksum_and_balance = 'PASS for every row'
    usb_open_count = $summary.usb_open_count
    actual_packets_sent = $summary.actual_packets_sent
    serial_connected = $summary.serial_connected
    armed = $summary.armed
}
$validation | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'SYNTHETIC-validation.json')
($scope + "`nPASS: fresh native/body axes, nonzero fixture attitude, PDF-direction pure-axis plateaus, independent matrix, balanced/checksummed preview packets, race-stop neutral, USB opens=0 and actual packets=0.") |
    Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'SYNTHETIC-validation.txt')
Get-Content -LiteralPath (Join-Path $OutputDirectory 'SYNTHETIC-validation.json')
