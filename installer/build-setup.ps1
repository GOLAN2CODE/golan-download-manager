# Builds GDMSetup.exe (next to the GDM folder): self-contained GDM.exe + Inno Setup wizard (Arabic/English).
# Needs .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$src = Join-Path $here "..\src\GDM\GDM.csproj"
$build = Join-Path $here "build"

dotnet publish $src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $build
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc (Join-Path $here "gdm.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

# Self-update payload: the same GDM.exe plus update.json (version, download address, SHA-256), uploaded to the
# GitHub release next to GDMSetup.exe. Golan reads .../releases/latest/download/update.json once a day.
$version = ([regex]::Match((Get-Content (Join-Path $here "gdm.iss") -Raw), '#define AppVersion "([^"]+)"')).Groups[1].Value
$outDir = Join-Path $here "..\.."
$payload = Join-Path $outDir "GDM-update.exe"
Copy-Item (Join-Path $build "GDM.exe") $payload -Force
$sha = (Get-FileHash $payload -Algorithm SHA256).Hash.ToLowerInvariant()
$feed = [ordered]@{
    version  = $version
    url      = "https://github.com/dark9guna/golan-download-manager-releases/releases/download/v$version/GDM-update.exe"
    sha256   = $sha
    notes_ar = "إصدار جديد من جولان: $version"
    notes_en = "New Golan release: $version"
}
$feed | ConvertTo-Json | Set-Content (Join-Path $outDir "update.json") -Encoding utf8
Write-Host "update payload $version sha256=$sha"
Remove-Item $build -Recurse -Force
