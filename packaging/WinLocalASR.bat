@echo off
rem Portable launcher for WinLocalASR (zip distribution, Task 13).
rem The exe is a self-contained single-file build: no .NET install required.
rem Keep this launcher next to WinLocalASR.App.exe.
start "" "%~dp0WinLocalASR.App.exe" %*
