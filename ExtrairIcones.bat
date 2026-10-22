@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ExtrairIcones.ps1"
echo.
echo Agora execute o Compilar.bat para embutir os icones no programa.
pause
