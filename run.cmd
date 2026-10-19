@echo off
title InstaMind
cd /d "%~dp0"
echo Starting InstaMind on http://localhost:5298 ...
dotnet run --urls http://localhost:5298
pause
