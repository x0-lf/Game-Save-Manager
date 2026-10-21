; Game Save Manager installer (RELEASE-001). Built by build/publish-release.ps1,
; which passes the version read from GameSaves.App.csproj and the publish folder.
; Per-user install without elevation, at a stable path under
; %LOCALAPPDATA%\Programs, so scheduled backup and sync tasks keep pointing at
; the installed executable across upgrades. The installer and the uninstaller
; never read, write or delete user data: the database, backups, settings,
; secrets and lock files live outside {app} and are left alone.

#ifndef AppVersion
  #error Pass /DAppVersion=MAJOR.MINOR.PATCH (build/publish-release.ps1 reads it from GameSaves.App.csproj)
#endif
#ifndef PublishDir
  #error Pass /DPublishDir=<self-contained win-x64 publish folder of GameSaves.App>
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#define AppName "Game Save Manager"
#define AppExeName "GameSaves.App.exe"

[Setup]
AppId={{AB36C1BF-B8DF-4E1B-819C-39CD9ECAE782}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={localappdata}\Programs\Game Save Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=GameSaveManager-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// User data lives outside {app} and is never installed, upgraded or removed
// by this package. After an interactive uninstall, say where it still is.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox('Game Save Manager was removed.' + #13#10#13#10 +
      'Your database, backups, settings and saved sign-ins were kept in:' + #13#10 +
      ExpandConstant('{localappdata}\GameSave') + #13#10#13#10 +
      'Delete that folder yourself if you no longer want them. ' +
      'Scheduled backups and syncs that pointed at the removed program stop working; ' +
      'remove them in Task Scheduler under "Game Save Manager".',
      mbInformation, MB_OK);
end;
