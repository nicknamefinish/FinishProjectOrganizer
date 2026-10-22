@echo off
setlocal
cd /d "%~dp0"
title Finish Project Organizer - Publicar versao
echo ============================================================
echo   FINISH : gerar a versao para publicar na internet
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo O .NET SDK 8 nao foi encontrado. Rode o Compilar.bat uma vez para instalar.
  pause
  exit /b 1
)

set "APPVER="
for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content FinishProjectOrganizer.csproj)).Project.PropertyGroup.Version"') do set "APPVER=%%v"
if not defined APPVER (
  echo Nao consegui ler a versao no FinishProjectOrganizer.csproj.
  pause
  exit /b 1
)
echo Versao: %APPVER%
echo.

if not exist "Fonts\Outfit-Regular.ttf" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BaixarFonte.ps1"
if not exist "Icons\*.png" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ExtrairIcones.ps1"

echo [1/3] Instalando/atualizando a ferramenta de empacotamento (vpk)...
dotnet tool update -g vpk
if errorlevel 1 dotnet tool install -g vpk
set "PATH=%PATH%;%USERPROFILE%\.dotnet\tools"

echo.
echo [2/3] Compilando o programa...
if exist "publish" rmdir /s /q "publish"
dotnet publish "FinishProjectOrganizer.csproj" -c Release -r win-x64 --self-contained true -o "publish"
if errorlevel 1 (
  echo Erro na compilacao.
  pause
  exit /b 1
)

echo.
echo [3/3] Criando o instalador e os pacotes de atualizacao...
vpk pack --packId FinishOrganizer --packVersion %APPVER% --packDir publish --mainExe FinishProjectOrganizer.exe --icon Assets\finish.ico --packTitle "Finish Project Organizer" --packAuthors Finish --outputDir Releases
if errorlevel 1 (
  echo Erro ao empacotar.
  pause
  exit /b 1
)

echo.
echo ============================================================
echo   Pronto! Os arquivos estao na pasta: %~dp0Releases
echo   Para publicar: crie uma "Release" no GitHub com a tag v%APPVER%
echo   e ARRASTE TODOS os arquivos da pasta Releases para ela.
echo   (Mais facil ainda: use o envio automatico, veja ONLINE.txt)
echo ============================================================
start "" explorer.exe "%~dp0Releases"
pause
