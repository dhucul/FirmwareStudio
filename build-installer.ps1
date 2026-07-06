<#
.SYNOPSIS
    Publishes FirmwareStudio (WPF, self-contained win-x64) and compiles the Inno Setup installer.

.DESCRIPTION
    One-shot build script so the installer can be rebuilt by hand. It:
      1. Publishes src/FirmwareStudio.Wpf as self-contained win-x64.
      2. Compiles installer/FirmwareStudio.iss with Inno Setup's ISCC.
      3. Reports the produced setup .exe in installer/Output.

    The installer version comes from #define MyAppVersion in the .iss; the app
    version comes from <Version> in FirmwareStudio.Wpf.csproj. Keep them in sync.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot

Write-Host "== FirmwareStudio installer build ==" -ForegroundColor Cyan

# 1. Publish the WPF app (self-contained so the target machine needs no .NET runtime).
$wpfProj = Join-Path $repo "src\FirmwareStudio.Wpf\FirmwareStudio.Wpf.csproj"
Write-Host "`n[1/2] dotnet publish ($Configuration, win-x64, self-contained)..." -ForegroundColor Yellow
dotnet publish $wpfProj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# 2. Compile the Inno Setup script.
$iss = Join-Path $repo "installer\FirmwareStudio.iss"
$iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    # Fall back to the machine-wide install location.
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
}
if (-not (Test-Path $iscc)) {
    throw "ISCC.exe not found. Install Inno Setup 6 (https://jrsoftware.org/isdl.php)."
}

Write-Host "`n[2/2] Compiling installer with ISCC..." -ForegroundColor Yellow
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

# 3. Report output.
$outDir = Join-Path $repo "installer\Output"
Write-Host "`n== Done. Output: ==" -ForegroundColor Green
Get-ChildItem $outDir -Filter *.exe | Sort-Object LastWriteTime -Descending |
    Select-Object Name, @{n="Size(MB)";e={[math]::Round($_.Length/1MB,1)}}, LastWriteTime |
    Format-Table -AutoSize
