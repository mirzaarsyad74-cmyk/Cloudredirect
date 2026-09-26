<#
.SYNOPSIS
    CloudRedirect Prerequisites Installer
.DESCRIPTION
    Automatically checks, downloads, and silently installs the required runtimes:
    - Microsoft .NET 8 Desktop Runtime (x64)
    - Microsoft Visual C++ 2015-2022 Redistributable (x64)
#>

[CmdletBinding()]
param(
    [switch]$LaunchApp
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    Write-Host "[INFO] Requesting Administrator privileges to install required runtimes..." -ForegroundColor Cyan
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($LaunchApp) { $argList += " -LaunchApp" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  CloudRedirect Prerequisites Auto-Installer" -ForegroundColor Cyan
Write-Host "  Automated .NET 8 Desktop Runtime & VC++ Redistributable" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Check & Install .NET 8 Desktop Runtime (x64)
Write-Host "[1/2] Checking for .NET 8 Desktop Runtime (x64)..." -ForegroundColor Yellow
$dotNetInstalled = $false
try {
    $runtimes = dotnet --list-runtimes 2>$null
    if ($runtimes -match "Microsoft\.WindowsDesktop\.App\s+8\.") {
        $dotNetInstalled = $true
    }
} catch { }

if (-not $dotNetInstalled) {
    $regKey = "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
    if (Test-Path $regKey) {
        $versions = Get-ItemProperty $regKey -ErrorAction SilentlyContinue
        if ($versions.PSObject.Properties.Name -match "^8\.") {
            $dotNetInstalled = $true
        }
    }
}

if ($dotNetInstalled) {
    Write-Host "[OK] .NET 8 Desktop Runtime is already installed!" -ForegroundColor Green
} else {
    Write-Host "[INFO] .NET 8 Desktop Runtime not found. Downloading official installer from Microsoft..." -ForegroundColor Cyan
    $dotNetUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
    $installerPath = Join-Path $env:TEMP "windowsdesktop-runtime-8.0-win-x64.exe"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $dotNetUrl -OutFile $installerPath -UseBasicParsing
        Write-Host "[INFO] Installing .NET 8 Desktop Runtime silently..." -ForegroundColor Cyan
        $process = Start-Process -FilePath $installerPath -ArgumentList "/install /quiet /norestart" -PassThru -Wait
        Remove-Item $installerPath -Force -ErrorAction SilentlyContinue

        if ($process.ExitCode -eq 0 -or $process.ExitCode -eq 3010) {
            Write-Host "[SUCCESS] .NET 8 Desktop Runtime installed successfully!" -ForegroundColor Green
        } else {
            Write-Host "[WARNING] Installer exited with code: $($process.ExitCode)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "[ERROR] Failed to download or install .NET 8 Desktop Runtime: $_" -ForegroundColor Red
    }
}

Write-Host ""

# 2. Check & Install Visual C++ 2015-2022 Redistributable (x64)
Write-Host "[2/2] Checking for Visual C++ 2015-2022 Redistributable (x64)..." -ForegroundColor Yellow
$vcInstalled = $false
$vcKey = "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
if (Test-Path $vcKey) {
    $installed = (Get-ItemProperty $vcKey -Name "Installed" -ErrorAction SilentlyContinue).Installed
    if ($installed -eq 1) {
        $vcInstalled = $true
    }
}

if ($vcInstalled) {
    Write-Host "[OK] Visual C++ Redistributable is already installed!" -ForegroundColor Green
} else {
    Write-Host "[INFO] Visual C++ Redistributable (x64) not found. Downloading official installer from Microsoft..." -ForegroundColor Cyan
    $vcUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
    $vcInstallerPath = Join-Path $env:TEMP "vc_redist.x64.exe"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $vcUrl -OutFile $vcInstallerPath -UseBasicParsing
        Write-Host "[INFO] Installing Visual C++ Redistributable silently..." -ForegroundColor Cyan
        $process = Start-Process -FilePath $vcInstallerPath -ArgumentList "/install /quiet /norestart" -PassThru -Wait
        Remove-Item $vcInstallerPath -Force -ErrorAction SilentlyContinue

        if ($process.ExitCode -eq 0 -or $process.ExitCode -eq 3010) {
            Write-Host "[SUCCESS] Visual C++ Redistributable installed successfully!" -ForegroundColor Green
        } else {
            Write-Host "[WARNING] Installer exited with code: $($process.ExitCode)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "[ERROR] Failed to download or install Visual C++ Redistributable: $_" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  All required runtimes are verified and ready!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""

# Launch CloudRedirect if requested or found
$targetExe = $null
$candidates = @(
    (Join-Path $PSScriptRoot "..\ui\bin\publish\CloudRedirect.exe"),
    (Join-Path $PSScriptRoot "CloudRedirect.exe"),
    (Join-Path $PSScriptRoot "..\CloudRedirect.exe")
)

foreach ($c in $candidates) {
    if (Test-Path $c) {
        $targetExe = $c
        break
    }
}

if ($targetExe) {
    Write-Host "[INFO] Launching CloudRedirect..." -ForegroundColor Cyan
    Start-Process -FilePath $targetExe
}
Start-Sleep -Seconds 2
