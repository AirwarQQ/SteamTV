@echo off
setlocal
rem Build console tester DisplayTest.exe (no SDK, .NET Framework only).
rem DisplayTest is built with actual Native.cs and DisplayManager.cs files —
rem they are C# 5 compatible, so the built-in csc.exe is sufficient.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" ( echo csc.exe not found (.NET Framework). & pause & exit /b 1 )

"%CSC%" /nologo /target:exe /out:DisplayTest.exe ^
  Tools\DisplayTest.cs Interop\Native.cs Services\DisplayManager.cs

if errorlevel 1 ( echo. & echo [!] Build failed. & pause & exit /b 1 )
echo.
echo [OK] Done: DisplayTest.exe
echo     Examples:  DisplayTest list   ^|   DisplayTest on 3   ^|   DisplayTest off 3
endlocal