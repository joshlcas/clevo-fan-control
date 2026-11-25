@echo off
REM Build FanControl.exe from FanControl.cs. See README "Build".
REM /target:winexe matters: a console build flashes a terminal next to the GUI.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found; install the .NET Framework 4.x or use Roslyn: 1>&2
  exit /b 1
)

cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:fancontrol.ico /out:FanControl.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll FanControl.cs
if errorlevel 1 exit /b 1
echo Built FanControl.exe
