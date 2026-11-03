@echo off
rem Builds MacBar.exe and MacBar-Setup.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set CSC=%FW%\csc.exe
set WPF=%FW%\WPF
rem WinRT metadata (Bluetooth/airplane radios, media controls) ships with Windows 10/11.
set WINMD=%WINDIR%\System32\WinMetadata
cd /d "%~dp0"
if not exist bin mkdir bin

set REFS=/r:"%WPF%\PresentationCore.dll" /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Web.Extensions.dll" /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Data.dll /r:"%FW%\System.Runtime.dll" /r:"%WINMD%\Windows.Foundation.winmd" /r:"%WINMD%\Windows.Devices.winmd" /r:"%WINMD%\Windows.Media.winmd" /r:"%WINMD%\Windows.Storage.winmd"

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
