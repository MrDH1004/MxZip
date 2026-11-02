; MxZip installer (Inno Setup 6). Per-user install: no administrator rights, no UAC prompt.
; build.ps1 passes AppVersion (from version.py):  ISCC /DAppVersion=1.2.3 installer\MxZip.iss
; First install registers the Explorer menu (and, if ticked, the archive file associations).
; Updates run this same setup with /VERYSILENT: it closes MxZip, replaces the files, re-registers the menu
; (the shell extension DLL copy is per build) and starts the new version with --updated.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; a new AppId: MxZip installs to its own folder; the yzip install (old AppId below) is removed first
AppId={{CC4D3DC6-E5BD-447F-B308-1990DFD75587}
AppName=MxZip
AppVersion={#AppVersion}
AppVerName=MxZip {#AppVersion}
AppPublisher=YMX
AppPublisherURL=https://github.com/MrDH1004/MxZip
AppSupportURL=https://github.com/MrDH1004/MxZip/issues
AppUpdatesURL=https://github.com/MrDH1004/MxZip/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=MxZip
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=MxZip setup
VersionInfoCopyright=Copyright (c) 2026 MrDH1004. MIT License.
DefaultDirName={localappdata}\Programs\MxZip
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=MxZip-setup-{#AppVersion}
SetupIconFile=..\MxZip.ico
UninstallDisplayIcon={app}\MxZip.exe
UninstallDisplayName=MxZip
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
InfoBeforeFile=INFO.txt
CloseApplications=force
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "assoc"; Description: "압축 파일(.zip, .7z, .tar 등)을 MxZip으로 열기"
Name: "autoupdate"; Description: "업데이트 자동 확인 (앱을 실행할 때마다 GitHub에 최신 버전 문의)"
Name: "desktopicon"; Description: "바탕 화면에 바로 가기 만들기"; Flags: unchecked

[InstallDelete]
; libraries of the previous build must not linger next to the new ones
Type: filesandordirs; Name: "{app}\_internal"
; shortcuts of the yzip name, in case the old uninstaller left them
Type: files; Name: "{userprograms}\yzip.lnk"
Type: files; Name: "{userdesktop}\yzip.lnk"

[Files]
Source: "..\dist\MxZip\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\MxZip"; Filename: "{app}\MxZip.exe"
Name: "{userdesktop}\MxZip"; Filename: "{app}\MxZip.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MxZip.exe"; Parameters: "--register-shell"; Flags: runhidden waituntilterminated
; these apply the tasks-page choices; an update keeps whatever the user set in the app since
; also after replacing a yzip install that had file associations (its uninstaller removed them)
Filename: "{app}\MxZip.exe"; Parameters: "--associate"; Flags: runhidden waituntilterminated; Check: ShouldAssociate
Filename: "{app}\MxZip.exe"; Parameters: "--auto-update on"; Tasks: autoupdate; Flags: runhidden waituntilterminated; Check: not IsUpdate
Filename: "{app}\MxZip.exe"; Parameters: "--auto-update off"; Tasks: not autoupdate; Flags: runhidden waituntilterminated; Check: not IsUpdate
Filename: "{app}\MxZip.exe"; Description: "MxZip 실행"; Flags: nowait postinstall skipifsilent
Filename: "{app}\MxZip.exe"; Parameters: "--updated"; Flags: nowait; Check: IsUpdate

[UninstallRun]
Filename: "{app}\MxZip.exe"; Parameters: "--unregister-all"; Flags: runhidden waituntilterminated; RunOnceId: "mxzipUnregister"

[Code]
// The new version is started afterwards only for an update: MxZip's updater passes /UPDATE=1, and a silent
// run over an existing install counts too (1.0.0's updater did not pass the flag). A scripted silent first
// install does not open a window.
const
  UninstallRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\';
  // the app's former name: yzip, installed to {localappdata}\Programs\yzip under this AppId
  YzipAppId = '{B3F7E2A1-6C4D-4E8B-9A15-2D7C8E0F4A63}_is1';

var
  WasInstalled, HadYzipAssoc: Boolean;

function InitializeSetup: Boolean;
begin
  WasInstalled := RegKeyExists(HKCU, UninstallRoot + '{CC4D3DC6-E5BD-447F-B308-1990DFD75587}_is1');
  Result := True;
end;

// Before the files are copied: a yzip install is uninstalled silently. Its uninstaller runs
// "yzip.exe --unregister-all" (Explorer menu, file associations, HKCU\Software\yzip) and deletes
// {localappdata}\Programs\yzip; the settings in {localappdata}\yzip stay and MxZip copies them on first start.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Uninstaller: String;
  Code, Waited: Integer;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallRoot + YzipAppId, 'UninstallString', Uninstaller) then
  begin
    Uninstaller := RemoveQuotes(Uninstaller);
    if FileExists(Uninstaller) then
    begin
      HadYzipAssoc := RegKeyExists(HKCU, 'Software\yzipAssoc\Capabilities');
      Log('Removing yzip: ' + Uninstaller);
      if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        Log('yzip uninstaller did not start: ' + SysErrorMessage(Code))
      else
        Log('yzip uninstaller exit code: ' + IntToStr(Code));
      // the uninstaller hands over to a temporary copy of itself; wait until it has removed its entry
      Waited := 0;
      while RegKeyExists(HKCU, UninstallRoot + YzipAppId) and (Waited < 60000) do
      begin
        Sleep(250);
        Waited := Waited + 250;
      end;
    end;
  end;
end;

function IsUpdate: Boolean;
begin
  Result := (ExpandConstant('{param:UPDATE|0}') = '1') or (WizardSilent and WasInstalled);
end;

function ShouldAssociate: Boolean;
begin
  Result := HadYzipAssoc or ((not IsUpdate) and WizardIsTaskSelected('assoc'));
end;

[UninstallDelete]
; copies of the Explorer extension (one per build); a copy Explorer still has loaded cannot be deleted
; at that moment and is left behind
Type: filesandordirs; Name: "{localappdata}\MxZip\shell"
