@echo off
rem Builds DesktopRecorder.exe with the C# compiler that ships with Windows, and gets ffmpeg if it is missing.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo The C# compiler was not found in %WINDIR%\Microsoft.NET.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0DesktopRecorder.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0DesktopRecorder.cs"
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Built %~dp0DesktopRecorder.exe

if exist "%~dp0ffmpeg.exe" goto done
where ffmpeg >nul 2>nul
if not errorlevel 1 goto done
if exist "%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe" goto done
echo ffmpeg was not found. Installing it with winget...
winget install -e --id Gyan.FFmpeg --accept-source-agreements --accept-package-agreements
if errorlevel 1 echo Could not install ffmpeg. Download ffmpeg.exe and put it next to DesktopRecorder.exe.

:done
echo Done. Start DesktopRecorder.exe.
pause
