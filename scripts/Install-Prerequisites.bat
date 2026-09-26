@echo off
setlocal enabledelayedexpansion
title CloudRedirect - Prerequisites Setup

echo ============================================================
echo   CloudRedirect Prerequisites Auto-Installer
echo   Automated .NET 8 Desktop Runtime ^& VC++ Redistributable
echo ============================================================
echo.

:: Check for administrative privileges
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [INFO] Requesting Administrator privileges to install required runtimes...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"%~f0\"' -Verb RunAs"
    exit /b
)

:: 1. Check for .NET 8 Desktop Runtime (x64)
echo [1/2] Checking for .NET 8 Desktop Runtime (x64)...
set "DOTNET_INSTALLED=0"
for /f "tokens=*" %%a in ('reg query "HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" 2^>nul ^| findstr /r "8\.[0-9]"') do (
    set "DOTNET_INSTALLED=1"
)

if "%DOTNET_INSTALLED%"=="1" (
    echo [OK] .NET 8 Desktop Runtime is already installed!
) else (
    echo [INFO] .NET 8 Desktop Runtime not found.
    echo [INFO] Downloading official installer from Microsoft CDN...
    set "DOTNET_URL=https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
    set "DOTNET_EXE=%TEMP%\windowsdesktop-runtime-8.0-win-x64.exe"
    powershell -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; (New-Object Net.WebClient).DownloadFile('%DOTNET_URL%', '%DOTNET_EXE%')"
    if exist "%DOTNET_EXE%" (
        echo [INFO] Installing .NET 8 Desktop Runtime silently...
        "%DOTNET_EXE%" /install /quiet /norestart
        del /f /q "%DOTNET_EXE%" >nul 2>&1
        echo [SUCCESS] .NET 8 Desktop Runtime installed successfully!
    ) else (
        echo [ERROR] Failed to download .NET 8 Desktop Runtime. Please check your internet connection.
    )
)
echo.

:: 2. Check for Visual C++ 2015-2022 Redistributable (x64)
echo [2/2] Checking for Visual C++ 2015-2022 Redistributable (x64)...
set "VCREDIST_INSTALLED=0"
for /f "tokens=*" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" /v "Installed" 2^>nul ^| findstr "0x1"') do (
    set "VCREDIST_INSTALLED=1"
)

if "%VCREDIST_INSTALLED%"=="1" (
    echo [OK] Visual C++ Redistributable is already installed!
) else (
    echo [INFO] Visual C++ Redistributable (x64) not found.
    echo [INFO] Downloading official installer from Microsoft...
    set "VC_URL=https://aka.ms/vs/17/release/vc_redist.x64.exe"
    set "VC_EXE=%TEMP%\vc_redist.x64.exe"
    powershell -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; (New-Object Net.WebClient).DownloadFile('%VC_URL%', '%VC_EXE%')"
    if exist "%VC_EXE%" (
        echo [INFO] Installing Visual C++ Redistributable silently...
        "%VC_EXE%" /install /quiet /norestart
        del /f /q "%VC_EXE%" >nul 2>&1
        echo [SUCCESS] Visual C++ Redistributable installed successfully!
    ) else (
        echo [ERROR] Failed to download Visual C++ Redistributable.
    )
)
echo.

echo ============================================================
echo   All required software is ready!
echo ============================================================
echo.

:: 3. Launch CloudRedirect if found nearby
if exist "%~dp0..\ui\bin\publish\CloudRedirect.exe" (
    echo Launching CloudRedirect...
    start "" "%~dp0..\ui\bin\publish\CloudRedirect.exe"
) else if exist "%~dp0CloudRedirect.exe" (
    echo Launching CloudRedirect...
    start "" "%~dp0CloudRedirect.exe"
)

timeout /t 3 >nul
exit /b 0
