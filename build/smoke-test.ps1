# End-to-end test on the build machine: install the server silently, talk to it like the sim PC
# would (discovery, voice round trip), install the sim app silently, then uninstall both.
param([Parameter(Mandatory)][string]$Dist)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$log = Join-Path $env:TEMP "LAIA-Setup.log"
$work = Join-Path $env:TEMP "laia-smoke"
New-Item -ItemType Directory -Force $work | Out-Null

function Step($m) { Write-Host "`n=== $m" -ForegroundColor Cyan }
function Fail($m) { Write-Host "SMOKE FAIL: $m" -ForegroundColor Red; if (Test-Path $log) { Get-Content $log | Select-Object -Last 200 }; $sl = "C:\ProgramData\LAIA\logs\server.log"; if (Test-Path $sl) { Write-Host "--- server.log"; Get-Content $sl | Select-Object -Last 200 }; exit 1 }

Step "Install LAIA Server silently"
$t = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process "$Dist\LAIA-Server-Setup.exe" -ArgumentList "/silent" -Wait -PassThru
Write-Host "exit $($p.ExitCode) after $([int]$t.Elapsed.TotalSeconds)s"
Get-Content $log | Select-Object -Last 60
if ($p.ExitCode -ne 0) { Fail "server setup returned $($p.ExitCode)" }

Step "Service and firewall"
sc.exe query LAIA
netsh advfirewall firewall show rule name="LAIA Server"

Step "Wait for speech engine"
$ready = $false
for ($i = 0; $i -lt 150; $i++) {
    try {
        $s = Invoke-RestMethod "http://127.0.0.1:47801/api/status" -TimeoutSec 5
        Write-Host "  $($s.speech.status) $($s.speech.detail)"
        if ($s.speech.status -eq "ready") { $ready = $true; break }
    } catch { Write-Host "  (no answer yet)" }
    Start-Sleep 4
}
if (-not $ready) { Fail "speech engine never became ready" }
$s | ConvertTo-Json -Depth 5

Step "Discovery (what LAIA Sim does to find the server)"
$udp = New-Object Net.Sockets.UdpClient
$udp.Client.ReceiveTimeout = 4000
$q = [Text.Encoding]::UTF8.GetBytes("LAIA_DISCOVER_V1")
[void]$udp.Send($q, $q.Length, "127.0.0.1", 47800)
$ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
$reply = [Text.Encoding]::UTF8.GetString($udp.Receive([ref]$ep))
$udp.Dispose()
Write-Host "reply from $ep : $reply"
if ($reply -notmatch '"App":"LAIA"') { Fail "bad discovery reply" }

Step "Control page"
$html = Invoke-WebRequest "http://127.0.0.1:47801/" -UseBasicParsing
if ($html.Content -notmatch "LAIA Server") { Fail "control page missing" }

Step "Voice round trip"
$call = "$work\call.wav"; $answer = "$work\reply.wav"
Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47801/api/say" -ContentType "application/json" `
    -Body '{"text":"LaGuardia Ground, Delta twelve thirty four, ready to taxi with information Bravo.","radio":true}' -OutFile $call
Write-Host "pilot call wav: $((Get-Item $call).Length) bytes"
$t = [Diagnostics.Stopwatch]::StartNew()
$r = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47801/api/radio" -ContentType "audio/wav" -InFile $call -OutFile $answer -PassThru -Headers @{ "X-LAIA-Client" = "SMOKE-TEST" }
$heard = [Uri]::UnescapeDataString("$($r.Headers['X-LAIA-Heard'])")
$said = [Uri]::UnescapeDataString("$($r.Headers['X-LAIA-Reply'])")
Write-Host "round trip: $([int]$t.Elapsed.TotalMilliseconds) ms"
Write-Host "HEARD: $heard"
Write-Host "REPLY: $said"
Write-Host "reply wav: $((Get-Item $answer).Length) bytes"
if ($said -notmatch "^Delta") { Fail "reply didn't address Delta" }
Copy-Item $call, $answer $env:SMOKE_OUT -ErrorAction SilentlyContinue

Step "Stray key-up (silence) gets no reply"
$silent = "$work\silent.wav"
$fs = [IO.File]::Create($silent); $bw = New-Object IO.BinaryWriter($fs)
$n = 16000; $dataLen = $n * 2
$bw.Write([Text.Encoding]::ASCII.GetBytes("RIFF")); $bw.Write([int](36 + $dataLen)); $bw.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
$bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1); $bw.Write([int]16000); $bw.Write([int]32000); $bw.Write([int16]2); $bw.Write([int16]16)
$bw.Write([Text.Encoding]::ASCII.GetBytes("data")); $bw.Write([int]$dataLen); $bw.Write((New-Object byte[] $dataLen)); $bw.Dispose()
$r = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47801/api/radio" -ContentType "audio/wav" -InFile $silent -SkipHttpErrorCheck
Write-Host "silence -> HTTP $($r.StatusCode)"
if ($r.StatusCode -ne 204) { Fail "silence should get no reply (204), got $($r.StatusCode)" }

Step "No facility named: reply has no made-up station name"
$call2 = "$work\call2.wav"
Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47801/api/say" -ContentType "application/json" `
    -Body '{"text":"Delta twelve thirty four, with you.","radio":true}' -OutFile $call2
$r = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47801/api/radio" -ContentType "audio/wav" -InFile $call2 -OutFile "$work\reply2.wav" -PassThru
$said2 = [Uri]::UnescapeDataString("$($r.Headers['X-LAIA-Reply'])")
Write-Host "REPLY: $said2"
if ($said2 -match "LAIA") { Fail "reply used LAIA as a station name" }

Step "Settings: save, read back masked, stored encrypted"
$body = '{"simbrief_user":"smoketest","schedule_source":"airlabs","airlabs_key":"SMOKEKEY123456"}'
$st = Invoke-RestMethod -Method Put -Uri "http://127.0.0.1:47801/api/settings" -ContentType "application/json" -Body $body
$st | ConvertTo-Json -Depth 4
if ($st.simbrief_user -ne "smoketest" -or $st.schedule_source -ne "airlabs" -or -not $st.airlabs.set -or $st.airlabs.hint -ne ([string][char]0x2026 + "3456")) { Fail "settings didn't round-trip" }
$raw = Get-Content "C:\ProgramData\LAIA\settings.json" -Raw
if ($raw -match "SMOKEKEY123456") { Fail "API key stored in plain text" }
if ($raw -notmatch "dpapi:") { Fail "API key not encrypted" }
$t = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47801/api/settings/test/airlabs" -ContentType "application/json" -Body '{"value":""}'
Write-Host "airlabs test with fake key: $($t.ok) $($t.message)"
if ($t.ok) { Fail "fake AirLabs key reported as working" }
$st = Invoke-RestMethod -Method Put -Uri "http://127.0.0.1:47801/api/settings" -ContentType "application/json" -Body '{"airlabs_key":""}'
if ($st.airlabs.set) { Fail "key removal failed" }

Step "FAA frequency data"
$fs = ""
for ($i = 0; $i -lt 60; $i++) {
    $fs = (Invoke-RestMethod "http://127.0.0.1:47801/api/status").flight.frequencies
    if ($fs -match "FAA cycle") { break }
    Start-Sleep 3
}
Write-Host "frequencies: $fs"
if ($fs -notmatch "FAA cycle") { Fail "FAA frequency data never loaded" }
$lga = Invoke-RestMethod "http://127.0.0.1:47801/api/airport/KLGA/frequencies"
$lga.frequencies | Format-Table | Out-String | Write-Host
$gnd = ($lga.frequencies | Where-Object { $_.kind -eq "Ground" -and $_.primary } | Select-Object -First 1).freqText
$cd = ($lga.frequencies | Where-Object { $_.kind -eq "Clearance" } | Select-Object -First 1).freqText
Write-Host "LGA ground $gnd, clearance $cd"
if (-not $gnd -or -not $cd) { Fail "LaGuardia ground/clearance frequencies not found" }

Step "Simulated aircraft at LaGuardia on the ground frequency"
function SimState($freq) {
    $o = @{ connected = $true; simulator = "MSFS 2024"; lat = 40.7769; lon = -73.8740; altitudeFt = 21; onGround = $true;
            com1Active = [double]$freq; com1Transmit = $true; headingMag = 40; title = "Smoke Test A320" }
    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47801/api/sim/state" -ContentType "application/json" -Body ($o | ConvertTo-Json) | Out-Null
}
SimState $gnd
$st = Invoke-RestMethod "http://127.0.0.1:47801/api/status"
Write-Host "flight: $($st.flight | ConvertTo-Json -Depth 4 -Compress)"
if ($st.flight.radio.station -notmatch "Ground") { Fail "ground frequency not recognised: $($st.flight.radio.station)" }

$call3 = "$work\call3.wav"
Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47801/api/say" -ContentType "application/json" `
    -Body '{"text":"LaGuardia Ground, Delta eleven ninety six, request I F R clearance to Orlando.","radio":true}' -OutFile $call3
$r = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47801/api/radio" -ContentType "audio/wav" -InFile $call3 -OutFile "$work\reply3.wav" -PassThru
$said3 = [Uri]::UnescapeDataString("$($r.Headers['X-LAIA-Reply'])")
Write-Host "HEARD: $([Uri]::UnescapeDataString("$($r.Headers['X-LAIA-Heard'])"))"
Write-Host "REPLY: $said3"
if ($said3 -notmatch "contact LaGuardia Clearance") { Fail "clearance request on ground frequency wasn't redirected" }

Step "Frequency with no controller"
SimState 123.450
$r = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:47801/api/radio" -ContentType "audio/wav" -InFile $call3 -SkipHttpErrorCheck
Write-Host "HTTP $($r.StatusCode) note: $([Uri]::UnescapeDataString("$($r.Headers['X-LAIA-Note'])"))"
if ($r.StatusCode -ne 204) { Fail "a frequency with no ATC still got a reply" }
$o = @{ connected = $false }
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:47801/api/sim/state" -ContentType "application/json" -Body ($o | ConvertTo-Json) | Out-Null

Step "Install LAIA Sim silently"
$p = Start-Process "$Dist\LAIA-Sim-Setup.exe" -ArgumentList "/silent" -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "sim setup returned $($p.ExitCode)" }
$exe = Join-Path $env:LOCALAPPDATA "Programs\LAIA Sim\LaiaSim.exe"
if (-not (Test-Path $exe)) { Fail "LaiaSim.exe not installed" }
$run = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LAIA Sim" -ErrorAction SilentlyContinue
if (-not $run) { Fail "autostart not set" }
Write-Host "installed $exe"

Step "Uninstall both"
$p = Start-Process (Join-Path $env:LOCALAPPDATA "Programs\LAIA Sim\uninstall.exe") -ArgumentList "/silent" -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "sim uninstall returned $($p.ExitCode)" }
$p = Start-Process "C:\Program Files\LAIA Server\uninstall.exe" -ArgumentList "/silent /purge" -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "server uninstall returned $($p.ExitCode)" }
Start-Sleep 2
sc.exe query LAIA | Out-Null
if ($LASTEXITCODE -eq 0) { Fail "service still registered after uninstall" }
if (Test-Path "C:\Program Files\LAIA Server\app") { Fail "program files left behind" }
Write-Host "`nSMOKE PASS" -ForegroundColor Green
exit 0
