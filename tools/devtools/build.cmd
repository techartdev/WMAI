@echo off
rem Builds the on-device helpers (NETCF 3.5) into out\:
rem   Run.exe       runs a program, waits, logs its exit code (run.log)
rem   RegDword.exe  sets a registry DWORD from a process on the device
rem Deploy with: python tools\wm.py push out\Run.exe "\Storage Card\WMAI\devtools\Run.exe"
set FW2=C:\Windows\Microsoft.NET\Framework\v2.0.50727
set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
if not exist out mkdir out
for %%t in (Run RegDword) do (
  "%CSC%" -nologo -noconfig -nostdlib -target:exe -optimize -out:out\%%t.exe ^
    -r:"%FW2%\mscorlib.dll" -r:"%FW2%\System.dll" %%t.cs || exit /b 1
  pwsh -NoProfile -File ..\..\app\retarget.ps1 out\%%t.exe > nul || exit /b 1
)
pwsh -NoProfile -File ..\cfcheck.ps1 out\Run.exe out\RegDword.exe
