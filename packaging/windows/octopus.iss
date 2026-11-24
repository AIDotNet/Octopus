; Inno Setup script for the Windows installer. Built by CI (release.yml); locally:
;   iscc /DAppVersion=0.1.0 /DSourceDir=<publish dir> /DOutputDir=<out dir> packaging\windows\octopus.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir is required: the self-contained publish output of src/Octopus.App
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
; Never change AppId: it is how upgrades and the uninstaller find an existing install.
AppId={{6F1D2B52-6B4A-4C57-9E0B-3A1F6C8B7D41}
AppName=Octopus
AppVersion={#AppVersion}
AppPublisher=AIDotNet
AppPublisherURL=https://github.com/AIDotNet/Octopus
AppSupportURL=https://github.com/AIDotNet/Octopus/issues
; The install directory page is always shown, and an upgrade starts from the previous choice.
DefaultDirName={autopf}\Octopus
DisableDirPage=no
UsePreviousAppDir=yes
DefaultGroupName=Octopus
DisableProgramGroupPage=yes
; Per-user by default (no UAC); the first page lets the user switch to an all-users install under Program Files.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#SourcePath}\..\..\src\Octopus.App\Assets\Icon\octopus.ico
UninstallDisplayIcon={app}\Octopus.exe
OutputDir={#OutputDir}
OutputBaseFilename=Octopus-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Octopus"; Filename: "{app}\Octopus.exe"
Name: "{autodesktop}\Octopus"; Filename: "{app}\Octopus.exe"; Tasks: desktopicon

[Registry]
; Register the octopus:// custom URL protocol (e.g. octopus://auth-done after browser sign-in).
Root: HKA; Subkey: "Software\Classes\octopus"; ValueType: string; ValueName: ""; ValueData: "URL:Octopus Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\octopus"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\octopus\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Octopus.exe"",0"
Root: HKA; Subkey: "Software\Classes\octopus\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Octopus.exe"" ""%1"""

[Run]
Filename: "{app}\Octopus.exe"; Description: "{cm:LaunchProgram,Octopus}"; Flags: nowait postinstall skipifsilent
; Automatic updates run setup silently; /RELAUNCH=1 ("restart and update") starts the new version afterwards.
Filename: "{app}\Octopus.exe"; Flags: nowait runasoriginaluser; Check: WantsRelaunch

[UninstallRun]
; Remove hooks/bridges written to external agent configs (e.g. ~/.gemini/config/hooks.json, Claude statusLine)
; and generated signal-attach files before deleting {app}.
Filename: "{app}\Octopus.exe"; Parameters: "--cleanup-hooks"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "CleanupHooks"

[Code]
function WantsRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;
