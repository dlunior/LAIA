# Publishes every project and produces the two installers:
#   out\dist\LAIA-Server-Setup.exe   (ATC PC)
#   out\dist\LAIA-Sim-Setup.exe      (flight sim PC)
# Each installer is the setup program with a zip of the app appended to the end.
param([string]$Version = "0.1.0", [string]$PythonDir = "")
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Resolve-Path "$PSScriptRoot\.."
$out = Join-Path $root "out"
Set-Location $root
Remove-Item -Recurse -Force "$out\server-payload", "$out\sim-payload", "$out\setup-server", "$out\setup-sim", "$out\dist" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$out\dist" | Out-Null

function Publish([string]$proj, [string]$dest, [string[]]$extra) {
    Write-Host "== publish $proj"
    & dotnet publish $proj -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $dest @extra
    if ($LASTEXITCODE) { throw "publish $proj failed" }
}

Publish "src\Laia.Server\Laia.Server.csproj" "$out\server-payload\app" @("-p:DebugType=none")
Publish "src\Laia.Sim\Laia.Sim.csproj" "$out\sim-payload" @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none")
Publish "src\Laia.Setup.Server\Laia.Setup.Server.csproj" "$out\setup-server" @()
Publish "src\Laia.Setup.Sim\Laia.Setup.Sim.csproj" "$out\setup-sim" @()

# Speech engine scripts + private Python
New-Item -ItemType Directory -Force "$out\server-payload\speech", "$out\server-payload\redist" | Out-Null
Copy-Item "speech\*.py", "speech\requirements.txt" "$out\server-payload\speech\"
if ($PythonDir) { Copy-Item $PythonDir "$out\server-payload\python" -Recurse }
Invoke-WebRequest "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile "$out\server-payload\redist\vc_redist.x64.exe"

# The setup stub (no payload) doubles as the uninstaller.
Copy-Item "$out\setup-server\LAIA-Server-Setup.exe" "$out\server-payload\uninstall.exe"
Copy-Item "$out\setup-sim\LAIA-Sim-Setup.exe" "$out\sim-payload\uninstall.exe"

function Make-Installer([string]$stub, [string]$payloadDir, [string]$dest) {
    Write-Host "== packing $dest"
    $zip = "$dest.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    [IO.Compression.ZipFile]::CreateFromDirectory($payloadDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item $stub $dest -Force
    $fs = [IO.File]::Open($dest, [IO.FileMode]::Append)
    try {
        $z = [IO.File]::OpenRead($zip)
        try { $z.CopyTo($fs); $len = $z.Length } finally { $z.Dispose() }
        $fs.Write([BitConverter]::GetBytes([long]$len), 0, 8)
        $magic = [Text.Encoding]::ASCII.GetBytes("LAIAPAY1")
        $fs.Write($magic, 0, 8)
    } finally { $fs.Dispose() }
    Remove-Item $zip
    Write-Host ("   {0:N0} MB" -f ((Get-Item $dest).Length / 1MB))
}

Make-Installer "$out\setup-server\LAIA-Server-Setup.exe" "$out\server-payload" "$out\dist\LAIA-Server-Setup.exe"
Make-Installer "$out\setup-sim\LAIA-Sim-Setup.exe" "$out\sim-payload" "$out\dist\LAIA-Sim-Setup.exe"
