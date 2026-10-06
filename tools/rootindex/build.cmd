@echo off
rem Builds app\WMAI.roots.idx from Mozilla's CA bundle (as published by the curl
rem project) with the same Bouncy Castle build as the phone, so the certificate
rem name strings match what WMAI computes on the device.
set FW2=C:\Windows\Microsoft.NET\Framework\v2.0.50727
set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
call ..\..\vendor\build-bc.cmd || exit /b 1
curl -sSfL -o cacert.pem https://curl.se/ca/cacert.pem || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:exe -optimize -out:RootIndex.exe ^
  -r:"%FW2%\mscorlib.dll" -r:"%FW2%\System.dll" -r:..\..\vendor\out\ref\BouncyCastle.Crypto.dll RootIndex.cs || exit /b 1
copy /y ..\..\vendor\out\ref\BouncyCastle.Crypto.dll . > nul
RootIndex.exe cacert.pem ..\..\app\WMAI.roots.idx
