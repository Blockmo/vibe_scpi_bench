@echo off
rem 建置 AteBench.exe 到 dist 資料夾 (需要 .NET SDK；目標為 .NET Framework 4.8)
cd /d "%~dp0"
dotnet build AteBench\AteBench.csproj -c Release -o dist --nologo
