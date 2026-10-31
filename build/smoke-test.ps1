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
