; Instalador do Finish Project Organizer (Inno Setup 6)
; A versao vem do Compilar.bat (le o <Version> do .csproj), assim so existe um lugar para mudar.
; Instala por usuario (sem pedir senha de administrador) em %LOCALAPPDATA%\Programs

#ifndef AppVer
  #define AppVer "12.0.0"
#endif

[Setup]
AppId={{B7C1E5A2-4F3D-4C8B-9A6E-5F1D2C3B4A70}
AppName=Finish Project Organizer
AppVersion={#AppVer}
AppPublisher=Finish
DefaultDirName={autopf}\Finish Project Organizer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=installer
OutputBaseFilename=Setup_FinishProjectOrganizer
SetupIconFile=Assets\finish.ico
UninstallDisplayIcon={app}\FinishProjectOrganizer.exe
UninstallDisplayName=Finish Project Organizer
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "dist\FinishProjectOrganizer.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Finish Project Organizer"; Filename: "{app}\FinishProjectOrganizer.exe"
Name: "{autodesktop}\Finish Project Organizer"; Filename: "{app}\FinishProjectOrganizer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FinishProjectOrganizer.exe"; Description: "Abrir o Finish Project Organizer agora"; Flags: nowait postinstall skipifsilent
