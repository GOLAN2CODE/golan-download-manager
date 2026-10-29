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
Remove-Item $build -Recurse -Force
