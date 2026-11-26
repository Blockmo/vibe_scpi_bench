@echo off
cd /d "%~dp0"
if not exist dist\AteBench.exe call build.bat
start "" dist\AteBench.exe
