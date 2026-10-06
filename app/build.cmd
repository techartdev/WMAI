@echo off
rem Builds WMAI.exe for .NET Compact Framework 3.5 using the desktop C# compiler
rem against .NET 2.0 reference assemblies, then retarget.ps1 rewrites the
rem assembly references to the NETCF 3.5 identities.
rem Files the phone needs next to WMAI.exe (\Storage Card\WMAI):
rem   BouncyCastle.Crypto.dll (vendor\out\cf), WMAI.roots, WMAI.agent.json,
rem   and WMAI.config (provider + key) for direct mode.
set FW2=C:\Windows\Microsoft.NET\Framework\v2.0.50727
set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
call ..\vendor\build-bc.cmd || exit /b 1
python ..\relay\tools.py WMAI.agent.json || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:winexe -platform:anycpu -optimize -out:WMAI.exe -win32icon:WMAI.ico ^
  -r:"%FW2%\mscorlib.dll" -r:"%FW2%\System.dll" -r:"%FW2%\System.Drawing.dll" -r:"%FW2%\System.Windows.Forms.dll" ^
  -r:..\vendor\out\ref\BouncyCastle.Crypto.dll ^
  WMAI.cs Tools.cs CodeTools.cs Web.cs ImageTools.cs Https.cs Json.cs Agent.cs || exit /b 1
pwsh -NoProfile -File retarget.ps1 WMAI.exe || exit /b 1
rem Fail the build if WMAI.exe uses any API the phone's NETCF 3.5 lacks.
pwsh -NoProfile -File ..\tools\cfcheck.ps1 WMAI.exe
