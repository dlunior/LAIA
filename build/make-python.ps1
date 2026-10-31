# Builds a private, portable Python with the speech packages for the LAIA Server payload.
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$ver = "3.12.10"
$tmp = Join-Path ([IO.Path]::GetTempPath()) "laia-py"
New-Item -ItemType Directory -Force $Out, $tmp | Out-Null

Invoke-WebRequest "https://www.python.org/ftp/python/$ver/python-$ver-embed-amd64.zip" -OutFile "$tmp\py.zip"
Expand-Archive "$tmp\py.zip" -DestinationPath $Out -Force
# Enable site-packages in the embeddable distribution.
Set-Content -Path (Join-Path $Out "python312._pth") -Value "python312.zip`r`n.`r`nLib\site-packages`r`nimport site" -Encoding ascii

Invoke-WebRequest "https://bootstrap.pypa.io/get-pip.py" -OutFile "$tmp\get-pip.py"
& "$Out\python.exe" "$tmp\get-pip.py" --no-warn-script-location --disable-pip-version-check
if ($LASTEXITCODE) { throw "get-pip failed" }

& "$Out\python.exe" -m pip install --no-warn-script-location --disable-pip-version-check --prefer-binary -r "$PSScriptRoot\..\speech\requirements.txt"
if ($LASTEXITCODE) { throw "pip install failed" }
& "$Out\python.exe" -m pip list --disable-pip-version-check

Get-ChildItem $Out -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
$size = (Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Python environment: {0:N0} MB" -f $size)
