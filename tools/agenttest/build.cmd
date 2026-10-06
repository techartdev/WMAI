@echo off
rem Builds the test programs:
rem   out\     PC tests (desktop .NET; they call the real DeepSeek API, so pass
rem            relay\.env and the app folder):  AgentTest.exe, WebVisionTest.exe
rem   out\phone\ on-device tests (NETCF 3.5), deployed to \Storage Card\WMAI\
rem            agenttest or codetest and started over RAPI (tools\wm.py run):
rem            DeviceAgentTest.exe, DeviceWebVisionTest.exe, CodeTest.exe
set FW2=C:\Windows\Microsoft.NET\Framework\v2.0.50727
set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
set BC=..\..\vendor\out\ref\BouncyCastle.Crypto.dll
set APP=..\..\app
set SRC=%APP%\Agent.cs %APP%\Json.cs %APP%\Https.cs %APP%\Tools.cs %APP%\CodeTools.cs %APP%\Web.cs %APP%\ImageTools.cs
set REFS=-r:"%FW2%\mscorlib.dll" -r:"%FW2%\System.dll" -r:%BC%
cd /d "%~dp0"
call ..\..\vendor\build-bc.cmd || exit /b 1
if not exist out\phone mkdir out\phone
for %%t in (AgentTest WebVisionTest TrimTest) do (
  "%CSC%" -nologo -noconfig -nostdlib -target:exe -optimize -out:out\%%t.exe %REFS% %%t.cs %SRC% || exit /b 1
)
copy /y %BC% out\ > nul
for %%t in (DeviceAgentTest DeviceWebVisionTest CodeTest) do (
  "%CSC%" -nologo -noconfig -nostdlib -target:exe -optimize -out:out\phone\%%t.exe %REFS% %%t.cs %SRC% || exit /b 1
  pwsh -NoProfile -File %APP%\retarget.ps1 out\phone\%%t.exe > nul || exit /b 1
)
copy /y ..\..\vendor\out\cf\BouncyCastle.Crypto.dll out\phone\ > nul
pwsh -NoProfile -File ..\cfcheck.ps1 out\phone\DeviceAgentTest.exe out\phone\DeviceWebVisionTest.exe out\phone\CodeTest.exe
