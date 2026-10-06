@echo off
rem Builds Bouncy Castle 1.8.10 for NETCF 3.5. The source is downloaded from the
rem official GitHub tag and patched by fetch_bc.py on first use.
rem   out\ref\BouncyCastle.Crypto.dll  desktop-2.0 build: compile against this
rem   out\cf\BouncyCastle.Crypto.dll   retargeted copy: deploy this to the phone
rem Compiling against the retargeted copy would duplicate mscorlib references.
set FW2=C:\Windows\Microsoft.NET\Framework\v2.0.50727
set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
set HERE=%~dp0
if exist "%HERE%out\cf\BouncyCastle.Crypto.dll" if not "%1"=="force" exit /b 0
if not exist "%HERE%bc-csharp-release-1.8.10\crypto\src" python "%HERE%fetch_bc.py" || exit /b 1
if not exist "%HERE%out\ref" mkdir "%HERE%out\ref"
if not exist "%HERE%out\cf" mkdir "%HERE%out\cf"

pushd "%HERE%bc-csharp-release-1.8.10\crypto"
dir /s /b src\*.cs bzip2\*.cs > "%TEMP%\bcfiles.rsp"
"%CSC%" -nologo -noconfig -nostdlib -target:library -optimize -nowarn:0618,0612,1591,0168,0219 ^
  -define:NETCF_2_0 -out:"%HERE%out\ref\BouncyCastle.Crypto.dll" ^
  -r:"%FW2%\mscorlib.dll" -r:"%FW2%\System.dll" @"%TEMP%\bcfiles.rsp" || (popd & exit /b 1)
popd
copy /y "%HERE%out\ref\BouncyCastle.Crypto.dll" "%HERE%out\cf\" > nul
pwsh -NoProfile -File "%HERE%..\app\retarget.ps1" "%HERE%out\cf\BouncyCastle.Crypto.dll" || exit /b 1
pwsh -NoProfile -File "%HERE%..\tools\cfcheck.ps1" "%HERE%out\cf\BouncyCastle.Crypto.dll"
