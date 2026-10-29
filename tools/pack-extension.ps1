param(
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$GdmRoot = Split-Path -Parent $PSScriptRoot
$WorkspaceRoot = Split-Path -Parent $GdmRoot
$ExtSource = Join-Path $GdmRoot "extension"

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $GdmRoot "store\dist"
}

$Csproj = Join-Path $GdmRoot "src\GDM\GDM.csproj"
dotnet build $Csproj -v minimal --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Failed to build GDM.csproj"
}

$GdmDll = Get-ChildItem -Path (Join-Path $GdmRoot "src\GDM\bin") -Filter "GDM.dll" -Recurse |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $GdmDll) {
    throw "GDM.dll not found after build."
}

$TempRunner = Join-Path ([System.IO.Path]::GetTempPath()) ("gdm-pack-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $TempRunner | Out-Null
try {
    $RunnerCsproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <Reference Include="GDM">
      <HintPath>$GdmDll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"@
    $RunnerProgram = @"
using Golan.Gdm.Integration;
var res = ExtensionPackager.Build(args[0], args[1]);
Console.WriteLine($"Chrome/Edge ZIP: {res.ChromeEdgeZipPath}");
Console.WriteLine($"Firefox XPI:     {res.FirefoxXpiPath}");
"@
    Set-Content -Path (Join-Path $TempRunner "PackRunner.csproj") -Value $RunnerCsproj -Encoding UTF8
    Set-Content -Path (Join-Path $TempRunner "Program.cs") -Value $RunnerProgram -Encoding UTF8

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $OutDir = [System.IO.Path]::GetFullPath($OutDir)
    dotnet run --project (Join-Path $TempRunner "PackRunner.csproj") -- $ExtSource $OutDir
    if ($LASTEXITCODE -ne 0) {
        throw "ExtensionPackager.Build failed with exit code $LASTEXITCODE"
    }

    $StoreDist = [System.IO.Path]::GetFullPath((Join-Path $GdmRoot "store\dist"))
    if (-not [string]::Equals($OutDir.TrimEnd('\'), $StoreDist.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        New-Item -ItemType Directory -Force -Path $StoreDist | Out-Null
        Copy-Item -Path (Join-Path $OutDir "gdm-*-chrome-edge.zip") -Destination $StoreDist -Force
        Copy-Item -Path (Join-Path $OutDir "gdm-*-firefox.xpi") -Destination $StoreDist -Force
    }
}
finally {
    Remove-Item -Recurse -Force -Path $TempRunner -ErrorAction SilentlyContinue
}
Write-Host "Store packages written to: $OutDir"
