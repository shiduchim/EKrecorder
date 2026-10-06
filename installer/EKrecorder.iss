; EKrecorder installer (Inno Setup 6). Installs for the current user only: no administrator rights needed.
; Built by the GitHub Actions workflow:  iscc /DAppVersion=1.0.0 installer\EKrecorder.iss
;
; Code signing can be added later without changing anything else: configure a sign tool in Inno Setup
; (SignTool=... below) and sign EKrecorder.exe before this script runs.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\EKrecorder"
#endif

[Setup]
AppId={{7C1F0E2A-5B3D-4E8A-9F21-3A6D2B8C4E11}
AppName=EKrecorder
AppVersion={#AppVersion}
AppVerName=EKrecorder {#AppVersion}
AppPublisher=EKrecorder
VersionInfoVersion={#AppVersion}
VersionInfoProductName=EKrecorder
VersionInfoDescription=EKrecorder setup
DefaultDirName={autopf}\EKrecorder
DefaultGroupName=EKrecorder
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\publish\installer
OutputBaseFilename=EKrecorder-Setup-{#AppVersion}
SetupIconFile=..\src\EKrecorder\EKrecorder.ico
UninstallDisplayIcon={app}\EKrecorder.exe
UninstallDisplayName=EKrecorder
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; EKrecorder is closed by asking it to (it saves a running recording first), not by Windows' Restart Manager.
CloseApplications=no
RestartApplications=no

[Files]
Source: "{#SourceDir}\EKrecorder.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion
; Used before installing, to ask a running EKrecorder (installed or not) to save and exit.
Source: "{#SourceDir}\EKrecorder.exe"; DestDir: "{tmp}"; DestName: "EKrecorder-exit.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\EKrecorder"; Filename: "{app}\EKrecorder.exe"; Comment: "Screen and call recorder"

[Run]
Filename: "{app}\EKrecorder.exe"; Description: "Start EKrecorder now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\EKrecorder.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitEKrecorder"

[Registry]
; EKrecorder adds this value itself ("Start EKrecorder with Windows"); uninstalling removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "EKrecorder"; Flags: uninsdeletevalue dontcreatekey

[Code]
// A running EKrecorder saves any recording and exits before its files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('EKrecorder-exit.exe');
  Exec(ExpandConstant('{tmp}\EKrecorder-exit.exe'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Recordings are never touched. Settings and logs go only if the user says so; an unfinished recording in
// %LocalAppData%\EKrecorder\InProgress is always kept.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\EKrecorder');
    if DirExists(Data) and not UninstallSilent() then
    begin
      if MsgBox('Also remove EKrecorder''s settings and logs?' + #13#10 + 'Your recordings are not touched.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DeleteFile(Data + '\settings.json');
        DelTree(Data + '\Logs', True, True, True);
        DelTree(Data + '\Reports', True, True, True);
        RemoveDir(Data + '\InProgress');
        RemoveDir(Data);
      end;
    end;
  end;
end;
