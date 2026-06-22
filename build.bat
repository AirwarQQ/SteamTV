@echo off
setlocal
rem ============================================================
rem  Build WITHOUT Visual Studio and WITHOUT .NET SDK.
rem  Only .NET Framework 4.x installed (included in Windows 10/11).
rem  Just double-click this file or run from the console.
rem ============================================================

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found (.NET Framework). Install .NET Framework 4.x.
  pause & exit /b 1
)

set "ICON=/win32icon:start_icon.ico"

"%CSC%" /nologo /target:winexe /out:SteamTV.exe %ICON% ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll ^
  Program.cs AppSettings.cs ^
  Interop\Native.cs ^
  Services\DisplayManager.cs Services\Adb.cs Services\SteamHelper.cs Services\MonitorService.cs ^
  Ui\MainForm.cs

if errorlevel 1 ( echo. & echo [!] Build failed. & pause & exit /b 1 )
echo. & echo [OK] Done: SteamTV.exe
endlocal