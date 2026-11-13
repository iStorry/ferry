; Ferry for Windows: per-user setup (no admin). Built by installer/build.ps1 from tools/publish-app.ps1's output.
;   iscc /DAppVersion=0.2.0 /DAppDir=..\artifacts\publish\app-win-x64 ferry.iss
; Updates run it as: Ferry-x.y.z-setup.exe /SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppDir
  #define AppDir "..\artifacts\publish\app-win-x64"
#endif

[Setup]
AppId={{4F91F8A8-68D7-4FC7-AE78-4424D29F96EE}
AppName=Ferry
AppVersion={#AppVersion}
AppVerName=Ferry {#AppVersion}
AppPublisher=Jatinjit Singh
AppPublisherURL=https://github.com/iStorry/ferry
AppSupportURL=https://github.com/iStorry/ferry/issues
AppUpdatesURL=https://github.com/iStorry/ferry/releases
DefaultDirName={localappdata}\Programs\Ferry
DefaultGroupName=Ferry
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=Ferry-{#AppVersion}-setup
SetupIconFile=..\src\Ferry.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Ferry.exe
UninstallDisplayName=Ferry
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; the app holds this mutex while it runs (Program.cs); setup asks to close it, and waits for it after an update
AppMutex=Ferry
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; files of an earlier version that this one no longer has
Type: filesandordirs; Name: "{app}\*.dll"

[Icons]
Name: "{autoprograms}\Ferry"; Filename: "{app}\Ferry.exe"
Name: "{autodesktop}\Ferry"; Filename: "{app}\Ferry.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Ferry.exe"; Description: "Start Ferry"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Ferry.exe"; Flags: nowait; Check: Relaunch

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ After an update the app closes right after starting setup: give it up to a minute to pause and let go of its mutex. }
function InitializeSetup: Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while Relaunch and CheckForMutexes('Ferry') and (Waited < 120) do
  begin
    Sleep(500);
    Waited := Waited + 1;
  end;
  Result := True;
end;
