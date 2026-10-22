@echo off
setlocal EnableExtensions EnableDelayedExpansion
title AdBlocker - Setup and Launch
cd /d "%~dp0"
set "ROOT=%~dp0"
set "SVC=AdBlocker"
set "LOG=%~dp0setup.log"

 echo ============================================================
 echo                 ADBLOCKER SETUP
 echo ============================================================
echo Project folder: %ROOT%
echo Log file:      %LOG%
echo.
>>"%LOG%" echo.
>>"%LOG%" echo ===== Run started %DATE% %TIME% =====

REM Elevate once because installing/updating a Windows service requires Administrator.
net session >nul 2>&1
if not errorlevel 1 goto :admin
if /i "%~1"=="--elevated" goto :elevation_failed

echo Administrator permission is required to install or update the Windows service.
echo A Windows User Account Control prompt should appear...
set "AD_RUN_BAT=%~f0"
set "AD_RUN_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Start-Process -FilePath $env:AD_RUN_BAT -Verb RunAs -WorkingDirectory $env:AD_RUN_DIR -ArgumentList '--elevated'; exit 0 } catch { Write-Error $_; exit 1 }"
if errorlevel 1 goto :elevation_failed
exit /b 0

:elevation_failed
echo.
echo [ERROR] Administrator permission was not granted or elevation could not start.
echo Right-click Run.bat and choose "Run as administrator".
call :log [ERROR] Elevation failed.
pause
exit /b 1

:admin
echo [1/6] Checking required files...
call :log [1/6] Checking required files
if not exist "%ROOT%Build.ps1" (
  echo [ERROR] Build.ps1 is missing. Extract the ENTIRE ZIP first.
  call :log [ERROR] Build.ps1 missing
  goto :fail
)
if not exist "%ROOT%Install-Service.ps1" (
  echo [ERROR] Install-Service.ps1 is missing. Extract the ENTIRE ZIP first.
  call :log [ERROR] Install-Service.ps1 missing
  goto :fail
)
if not exist "%ROOT%src\AdBlocker.Service\AdBlocker.Service.csproj" (
  echo [ERROR] Service source files are missing. Extract the ENTIRE ZIP first.
  call :log [ERROR] Service project missing
  goto :fail
)
if not exist "%ROOT%src\AdBlocker.App\AdBlocker.App.csproj" (
  echo [ERROR] Desktop app source files are missing.
  call :log [ERROR] App project missing
  goto :fail
)

REM Find .NET and require the .NET 8 SDK.
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"
if defined ProgramFiles(x86) if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" set "PATH=%ProgramFiles(x86)%\dotnet;%PATH%"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] .NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0
  call :log [ERROR] dotnet not found
  goto :fail
)
dotnet --list-sdks 2>nul | findstr /R /C:"^8\." >nul
if errorlevel 1 (
  echo [ERROR] .NET 8 SDK was not detected. Install it from https://dotnet.microsoft.com/download/dotnet/8.0
  call :log [ERROR] .NET 8 SDK not detected
  goto :fail
)
echo [OK] .NET 8 SDK detected.
call :log [OK] .NET 8 SDK detected

REM Always rebuild from the source beside this BAT; never use a hard-coded old project path.
echo.
echo [2/6] Building AdBlocker...
call :log [2/6] Building projects
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Build.ps1"
if errorlevel 1 (
  echo [ERROR] Build failed. Review the output above and setup.log.
  call :log [ERROR] Build.ps1 failed
  goto :fail
)
if not exist "%ROOT%bin\AdBlocker.Service.exe" (
  echo [ERROR] Build completed but bin\AdBlocker.Service.exe is missing.
  call :log [ERROR] Service executable missing after build
  goto :fail
)
if not exist "%ROOT%bin\AdBlocker.exe" (
  echo [ERROR] Build completed but bin\AdBlocker.exe is missing.
  call :log [ERROR] Desktop executable missing after build
  goto :fail
)
echo [OK] Build files are present.
call :log [OK] Build files are present

REM Migrate any differently named legacy service before repairing the current service.
echo.
echo [3/6] Checking for a previous Sam AdBlocker installation...
if exist "%ROOT%Migrate-From-SamAdBlocker.ps1" (
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Migrate-From-SamAdBlocker.ps1"
  if errorlevel 1 (
    echo [ERROR] Legacy migration failed. Read the message above before retrying.
    call :log [ERROR] Legacy migration failed
    goto :fail
  )
) else (
  echo [INFO] Migration script not present; skipping legacy migration.
)

REM Preserve the user's existing DNS-routing choice while re-registering the service.
set "DNS_ARG="
set "AD_SETTINGS=%ProgramData%\AdBlocker\settings.json"
if exist "%AD_SETTINGS%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue'; $p=$env:AD_SETTINGS; if (Test-Path -LiteralPath $p) { $s=Get-Content -Raw -LiteralPath $p | ConvertFrom-Json; if ($s.applySystemDns -eq $true) { exit 10 } }; exit 0"
  if errorlevel 10 set "DNS_ARG=-ApplySystemDns"
)

REM Important: -Force removes the stale service registration and registers the NEW bin path.
echo.
echo [4/6] Installing/updating the AdBlocker Windows service...
echo This updates an existing service that may still point to an older project folder.
call :log [4/6] Installing/updating service; DNS_ARG=%DNS_ARG%
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Install-Service.ps1" -NoPrompt -StartWithWindows -Force %DNS_ARG%
if errorlevel 1 (
  echo [ERROR] Service installation/update failed. See the error above.
  call :log [ERROR] Install-Service.ps1 failed
  goto :fail
)

 echo.
echo [5/6] Starting/checking the background service...
sc.exe start "%SVC%"
REM SC may report an error if it has not finished transitioning; verify actual state below.
set "SVC_RUNNING=0"
for /L %%I in (1,1,15) do (
  sc.exe query "%SVC%" 2>nul | findstr /C:"RUNNING" >nul
  if not errorlevel 1 set "SVC_RUNNING=1"
  if "!SVC_RUNNING!"=="1" goto :service_running
  timeout /t 1 /nobreak >nul
)
:service_running
if not "%SVC_RUNNING%"=="1" (
  echo [ERROR] Windows could not start the service. Service details:
  sc.exe qc "%SVC%"
  sc.exe query "%SVC%"
  echo Check Event Viewer ^> Windows Logs ^> Application for AdBlocker errors.
  call :log [ERROR] Service did not reach RUNNING state
  goto :fail
)
echo [OK] Windows service is running.
call :log [OK] Windows service is running

echo.
echo Checking the engine's actual status...
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Show-Status.ps1" -WaitSeconds 20
set "STATUS=%ERRORLEVEL%"
if not "%STATUS%"=="0" (
  echo.
  echo [ERROR] The engine did not confirm Active protection. The app will not be reported as protected.
  call :log [ERROR] Engine status not Active, exit code %STATUS%
  goto :fail
)
call :log [OK] Engine reports Active

echo.
echo [6/6] Opening the AdBlocker control panel...
start "AdBlocker" "%ROOT%bin\AdBlocker.exe"
if errorlevel 1 (
  echo [WARNING] The engine is active, but the UI could not be launched automatically.
  echo Try running: dotnet "%ROOT%bin\AdBlocker.dll"
  call :log [WARNING] UI launch command failed
)
echo.
echo ============================================================
echo SETUP COMPLETE - ENGINE REPORTED ACTIVE
echo ============================================================
echo The Windows service runs independently in the background.
echo Closing this launcher window does NOT stop the service.
echo Chrome extension setup is separate; see README.md.
echo DNS filtering cannot reliably block every YouTube in-stream ad.
call :log [OK] Setup completed with Active status
echo.
pause
exit /b 0

:fail
echo.
echo Setup did not complete successfully. Protection is NOT confirmed active.
echo Log: %LOG%
echo Keep this window open and share the error text or setup.log for troubleshooting.
call :log Setup failed
pause
exit /b 1

:log
>>"%LOG%" echo %DATE% %TIME% %*
exit /b 0