@echo off
setlocal
cd /d "%~dp0"
title Finish Project Organizer - Compilar
echo ============================================================
echo   FINISH - Project Organizer : gerando o programa e o instalador
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo O .NET SDK 8 nao foi encontrado neste computador.
  echo.
  choice /M "Instalar agora com o winget"
  if errorlevel 2 (
    echo Baixe em: https://dotnet.microsoft.com/download/dotnet/8.0  ^(SDK^)
    pause
    exit /b 1
  )
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
  echo.
  echo Instalacao concluida. FECHE esta janela e execute o Compilar.bat novamente.
  pause
  exit /b 0
)

set "APPVER="
for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content FinishProjectOrganizer.csproj)).Project.PropertyGroup.Version"') do set "APPVER=%%v"
if not defined APPVER set "APPVER=11.7.0"
echo Versao: %APPVER%
echo.

if not exist "Fonts\Outfit-Regular.ttf" (
  echo Baixando a fonte Outfit ^(licenca livre OFL^)...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BaixarFonte.ps1"
  if errorlevel 1 (
    echo [AVISO] Nao foi possivel baixar a fonte. O programa sera compilado com a fonte Segoe UI.
    echo         Se quiser, baixe a Outfit em fonts.google.com e coloque os .ttf na pasta Fonts.
  )
  echo.
)

if not exist "Icons\*.png" (
  echo Extraindo os icones dos programas instalados neste PC...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ExtrairIcones.ps1"
  echo.
)

echo [1/2] Compilando o programa ^(a primeira vez pode demorar alguns minutos^)...
dotnet publish "FinishProjectOrganizer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "dist"
if errorlevel 1 (
  echo.
  echo Ocorreu um erro na compilacao. Copie a mensagem acima e envie para ajuste.
  pause
  exit /b 1
)

echo.
echo [2/2] Criando o instalador...
call :findiscc
if defined ISCC goto :buildsetup

echo O Inno Setup ^(gratuito, cria o instalador^) nao foi encontrado.
choice /M "Instalar agora com o winget"
if errorlevel 2 goto :nosetup
winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
call :findiscc
if not defined ISCC goto :nosetup

:buildsetup
"%ISCC%" /DAppVer=%APPVER% "Installer.iss"
if errorlevel 1 goto :nosetup
echo.
echo ============================================================
echo   Pronto!
echo   Instalador para distribuir: %~dp0installer\Setup_FinishProjectOrganizer.exe
echo   Programa solto ^(sem instalar^): %~dp0dist\FinishProjectOrganizer.exe
echo ============================================================
start "" explorer.exe "%~dp0installer"
pause
exit /b 0

:nosetup
echo.
echo O instalador nao foi gerado. O programa solto esta em: %~dp0dist\FinishProjectOrganizer.exe
echo Para gerar o instalador, instale o Inno Setup ^(jrsoftware.org/isdl.php^) e rode o Compilar.bat de novo.
start "" explorer.exe "%~dp0dist"
pause
exit /b 0

:findiscc
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
exit /b 0
