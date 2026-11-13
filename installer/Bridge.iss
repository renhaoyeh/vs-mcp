#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef BuildId
  #error BuildId is required
#endif

[Setup]
AppId={{13710E01-3D60-4F54-8C60-9D94A824BBA9}
AppName=VS MCP Bridge
AppVersion={#AppVersion}
AppPublisher=VS MCP
DefaultDirName={localappdata}\Programs\VsMcp
DefaultGroupName=VS MCP Bridge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=VsMcp.Bridge.Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
DisableDirPage=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\VsMcp.Launcher.exe
SetupLogging=yes
CreateUninstallRegKey=not IsTestInstall

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#Launcher}"; DestDir: "{app}"; Flags: ignoreversion; Check: LauncherNeedsUpdate
; An immutable content-addressed payload never overwrites a running Bridge, even on reinstall.
Source: "{#Payload}\*"; DestDir: "{app}\versions\{#BuildId}"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs
Source: "{#Manifest}"; Flags: dontcopy

[Icons]
Name: "{group}\MCP configuration"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\mcp-config.json"""; Check: not IsTestInstall
Name: "{group}\Uninstall VS MCP Bridge"; Filename: "{uninstallexe}"; Check: not IsTestInstall

[Run]
Filename: "{sys}\notepad.exe"; Parameters: """{app}\mcp-config.json"""; Description: "Open MCP configuration (copy it into your MCP client)"; Flags: postinstall nowait skipifsilent; Check: InstallationVerified

[UninstallDelete]
Type: files; Name: "{app}\current.txt"
Type: files; Name: "{app}\mcp-config.json"

[Code]
var VerificationFailed: Boolean;

function InstallationVerified: Boolean;
begin
  Result := not VerificationFailed;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if VerificationFailed then Result := 1;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and VerificationFailed then
    WizardForm.FinishedLabel.Caption := 'Bridge verification failed. The previously selected version has been preserved. Review the Setup error and log before retrying.';
end;

function IsTestInstall: Boolean;
begin
  { Isolated smoke tests must not change the user's installed-app registration. }
  Result := ExpandConstant('{param:TestInstall|0}') = '1';
end;

function MoveFileEx(ExistingFileName, NewFileName: String; Flags: Cardinal): Boolean;
  external 'MoveFileExW@kernel32.dll stdcall';

function LauncherNeedsUpdate: Boolean;
var Target: String;
begin
  Target := ExpandConstant('{app}\VsMcp.Launcher.exe');
  Result := True;
  if FileExists(Target) then
    Result := GetSHA256OfFile(Target) <> '{#LauncherSha256}';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Lines: TArrayOfString;
  Index, Separator: Integer;
  RelativeName, Expected, Target, PointerTemp, Config: String;
begin
  if CurStep <> ssPostInstall then Exit;
  VerificationFailed := True;
  ExtractTemporaryFile('payload.sha256');
  if not LoadStringsFromFile(ExpandConstant('{tmp}\payload.sha256'), Lines) then
    RaiseException('Cannot read the Bridge payload manifest.');
  for Index := 0 to GetArrayLength(Lines) - 1 do begin
    Separator := Pos('|', Lines[Index]);
    if Separator <> 65 then RaiseException('Invalid Bridge payload manifest.');
    Expected := Copy(Lines[Index], 1, 64);
    RelativeName := Copy(Lines[Index], 66, MaxInt);
    Target := ExpandConstant('{app}\versions\{#BuildId}\') + RelativeName;
    if not FileExists(Target) then RaiseException('Missing Bridge file: ' + RelativeName);
    if GetSHA256OfFile(Target) <> Expected then
      RaiseException('Bridge verification failed: ' + RelativeName + '. The previously selected Bridge has been preserved.');
  end;
  Config := ExpandConstant('{app}\VsMcp.Launcher.exe');
  StringChangeEx(Config, '\', '\\', True);
  Config := '{' + #13#10 + '  "mcpServers": {' + #13#10 + '    "visual-studio": {' + #13#10 +
    '      "command": "' + Config + '"' + #13#10 + '    }' + #13#10 + '  }' + #13#10 + '}' + #13#10;
  if not SaveStringToFile(ExpandConstant('{app}\mcp-config.json'), UTF8Encode(Config), False) then
    RaiseException('MCP configuration could not be written.');
  { Same-volume rename is the final commit, after every payload file is verified. }
  PointerTemp := ExpandConstant('{app}\current-') + '{#BuildId}' + '.tmp';
  if not SaveStringToFile(PointerTemp, '{#BuildId}' + #13#10, False) then
    RaiseException('Cannot prepare the Bridge version pointer.');
  if not MoveFileEx(PointerTemp, ExpandConstant('{app}\current.txt'), 1 or 8) then
    RaiseException('Cannot select the new Bridge version. The previous selection has been preserved.');
  VerificationFailed := False;
end;

procedure InitializeWizard;
begin
  WizardForm.FinishedLabel.Caption :=
    'VS MCP Bridge is installed. Copy the visual-studio entry from mcp-config.json into your MCP client, preserving its other settings. Restart the MCP connection to use the new version.';
end;
