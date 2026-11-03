@echo off
rem Builds MacBar.exe and MacBar-Setup.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set CSC=%FW%\csc.exe
set WPF=%FW%\WPF
cd /d "%~dp0"
if not exist bin mkdir bin

set REFS=/r:"%WPF%\PresentationCore.dll" /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Web.Extensions.dll" /r:System.dll /r:System.Core.dll /r:System.Xml.dll

set ICON=
if exist assets\MacBar.ico set ICON=/win32icon:assets\MacBar.ico

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:bin\MacBar.exe %ICON% %REFS% src\*.cs
if errorlevel 1 exit /b 1
echo Built bin\MacBar.exe

if exist installer\Setup.cs (
  "%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:bin\MacBar-Setup.exe %ICON% /win32manifest:installer\setup.manifest /resource:bin\MacBar.exe,MacBar.exe /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll installer\Setup.cs
  if errorlevel 1 exit /b 1
  echo Built bin\MacBar-Setup.exe
)
