; DetailFrame installer. Compile with:
;   ISCC.exe /DAppVersion=<version> installer\DetailFrame.iss
; Inputs: artifacts\app\win-x64, artifacts\worker\win-x64, artifacts\installer\prereq (see README.md).
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define RepoRoot ExtractFilePath(RemoveBackslash(SourcePath))
#define AppExe "DetailFrame.App.exe"
#define RuntimeInstaller "WindowsAppRuntimeInstall-x64.exe"

[Setup]
AppId={{6B0E5C2D-3F7A-4B8E-9C41-D7A2F15E8B30}
AppName=DetailFrame
AppVersion={#AppVersion}
AppPublisher=DetailFrame
DefaultDirName={autopf}\DetailFrame
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
SetupIconFile={#RepoRoot}src\DetailFrame.App\Assets\Branding\app.ico
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#RepoRoot}artifacts\installer
OutputBaseFilename=DetailFrame-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "Shortcuts:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The payload is the application and CPU-only worker; model weights are never bundled.
Source: "{#RepoRoot}artifacts\app\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#RepoRoot}artifacts\worker\win-x64\*"; DestDir: "{app}\worker"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#RepoRoot}installer\prerequisites.ps1"; Flags: dontcopy
Source: "{#RepoRoot}artifacts\installer\prereq\{#RuntimeInstaller}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\DetailFrame"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: startmenuicon
Name: "{autodesktop}\DetailFrame"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch DetailFrame"; Flags: nowait postinstall skipifsilent

; Uninstall removes only files this installer placed under {app}. User settings and downloaded
; model weights live under %LOCALAPPDATA%\DetailFrame and are intentionally left in place.
; The shared Windows App SDK runtime is not removed because other applications may use it.

[Code]
function RunPrerequisites(const Extra: String; var ExitCode: Integer): Boolean;
var
  Args: String;
begin
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\prerequisites.ps1') + '" ' + Extra;
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function FailureText(Code: Integer): String;
begin
  case Code of
    10: Result := 'DetailFrame requires Windows 11.';
    11: Result := 'DetailFrame requires 64-bit (x64) Windows.';
    12: Result := 'The required Windows App SDK runtime 2.5.1 could not be installed. Install it from https://aka.ms/windowsappsdkinstall and run Setup again.';
  else
    Result := 'The system prerequisite check failed (code ' + IntToStr(Code) + ').';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  ExtractTemporaryFile('prerequisites.ps1');
  if not RunPrerequisites('-Mode Check -ProbeOnly', Code) then
  begin
    Result := 'Unable to run the prerequisite check.';
    Exit;
  end;
  if Code = 20 then
  begin
    WizardForm.StatusLabel.Caption := 'Installing Windows App SDK runtime...';
    ExtractTemporaryFile('{#RuntimeInstaller}');
    if not RunPrerequisites('-Mode Check -RuntimeInstaller "' + ExpandConstant('{tmp}\{#RuntimeInstaller}') + '"', Code) then
      Code := 12;
  end;
  if Code <> 0 then
    Result := FailureText(Code);
end;
