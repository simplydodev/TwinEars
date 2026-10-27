@echo off
rem Builds dist\TwinEars.exe with the C# compiler that ships inside Windows (.NET Framework 4.x).
rem Nothing needs to be installed.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework 4 C# compiler. It is part of Windows 10/11.
  exit /b 1
)

pushd "%~dp0"
if not exist dist mkdir dist
set "ICON="
if exist src\TwinEars.ico set "ICON=/win32icon:src\TwinEars.ico"

"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:4 ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /win32manifest:src\app.manifest %ICON% /out:dist\TwinEars.exe src\*.cs
set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo Build FAILED.
  exit /b %RC%
)
echo Built dist\TwinEars.exe
