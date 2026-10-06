@echo off
rem Builds NetTest.exe (app\Https.cs + NetTest.cs) twice:
rem   out\pc\    desktop build, runnable on this PC (compiled against BC's desktop-2.0 build)
rem   out\phone\ NETCF 3.5 build (retargeted copies) for the device
set FW2=C:\Windows\Microsoft.NET\Framework\v2.0.50727
set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
set BCREF=%~dp0..\..\vendor\out\ref\BouncyCastle.Crypto.dll
set BCCF=%~dp0..\..\vendor\out\cf\BouncyCastle.Crypto.dll
cd /d "%~dp0"
call ..\..\vendor\build-bc.cmd || exit /b 1
if not exist out\pc mkdir out\pc
if not exist out\phone mkdir out\phone

"%CSC%" -nologo -noconfig -nostdlib -target:exe -platform:anycpu -optimize -out:out\pc\NetTest.exe ^
  -r:"%FW2%\mscorlib.dll" -r:"%FW2%\System.dll" -r:"%BCREF%" NetTest.cs ..\..\app\Https.cs || exit /b 1
copy /y "%BCREF%" out\pc\ > nul
copy /y ..\..\app\WMAI.roots out\pc\ > nul

copy /y out\pc\NetTest.exe out\phone\ > nul
copy /y "%BCCF%" out\phone\ > nul
copy /y ..\..\app\WMAI.roots out\phone\ > nul
pwsh -NoProfile -File ..\..\app\retarget.ps1 out\phone\NetTest.exe || exit /b 1
pwsh -NoProfile -File ..\cfcheck.ps1 out\phone\NetTest.exe out\phone\BouncyCastle.Crypto.dll
