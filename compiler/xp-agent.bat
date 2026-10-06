@echo off
rem WMAI build agent for a Windows XP virtual machine (see compiler\README.md).
rem Runs jobs\job.bat whenever one appears (dropped in from the host via a
rem shared folder), writing its output to jobs\job.log and its exit code to
rem jobs\job.done. Close this window to stop it.
title WMAI build agent - close this window to stop
set Q=%~dp0jobs\
if not exist "%Q%" mkdir "%Q%"
echo WMAI build agent watching %Q%
echo Close this window to stop.
:loop
if not exist "%Q%job.bat" goto wait
if exist "%Q%job.done" del "%Q%job.done"
move /y "%Q%job.bat" "%Q%running.bat" >nul
echo [%date% %time%] running job...
call "%Q%running.bat" > "%Q%job.log" 2>&1
rem Not inside a ( ) block: %errorlevel% would be expanded before the job ran.
rem Redirect first: "echo 0> file" would redirect handle 0 instead.
>"%Q%job.done" echo %errorlevel%
del "%Q%running.bat"
echo [%date% %time%] job finished
:wait
ping -n 2 127.0.0.1 >nul
goto loop
