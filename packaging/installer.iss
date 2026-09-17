; WinLocalASR Inno Setup installer (Task 13).
;
; Hard contracts encoded here:
;   * settings.json is the autostart SSOT -- this installer NEVER writes or merges it.
;     Autostart = HKCU Run value (the derived half) + autostart-pending.flag; the
;     app's SettingsStore.MergePendingAutoStartFlag merges the SSOT on first launch
;     (Task 5 contract; flag file sits next to settings.json in %APPDATA%\WinLocalASR).
;   * Uninstall deliberately LEAVES %LOCALAPPDATA%\WinLocalASR (downloaded models,
;     ~2.5 GB, plus engine binaries) in place; the uninstaller says so (non-silent
;     installs only) -- deleting 2.5 GB of re-downloadable-but-expensive data
;     without asking is worse than leaving it.
;   * The AppId GUID below is FIXED for all future releases -- Inno uses it to
;     locate prior installs for in-place upgrades. Never regenerate it.
;   * Silent install forms: /VERYSILENT alone; with autostart via the built-in
;     /TASKS=autostart OR the /AUTOSTART=1 param alias (both handled below).
;
; Build: stage the self-contained single-file publish output into ..\staging\app
; (see .github/workflows/windows.yml), then ISCC /DMyAppVersion=x.y.z this file.

#ifndef MyAppVersion
#define MyAppVersion "0.1.0"
#endif

#define MyAppName "WinLocalASR"
#define MyAppExeName "WinLocalASR.App.exe"
#define MyAppId "{{CF74D530-587A-4944-9072-EC061A017589}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
UninstallDisplayName={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; Known boundary (prd): HKCU Run is written by the elevated installer into the
; elevating user's hive; if the install credential differs from the target user,
; autostart does not apply for the target user (single-user target machine assumption).
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\dist
OutputBaseFilename=WinLocalASR-Setup-v{#MyAppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start {#MyAppName} automatically when I sign in"; Flags: unchecked

[Files]
Source: "..\staging\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Code]
const
  RunKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run';
  // Must match Core AutoStartHelper.RunValueName (Task 11 pins value == exe path).
  RunValueName = 'WinLocalASR';
  PendingFlagDir = '{userappdata}\WinLocalASR';
  PendingFlagFile = 'autostart-pending.flag';

function AutostartRequested(): Boolean;
begin
  // Interactive: the [Tasks] checkbox. Silent: /TASKS=autostart selects the same
  // task; /AUTOSTART=1 is the documented param alias ({param:...} is Inno's
  // built-in custom-parameter constant).
  Result := WizardIsTaskSelected('autostart') or
            (ExpandConstant('{param:AUTOSTART|0}') = '1');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  FlagDir: string;
begin
  if (CurStep = ssPostInstall) and AutostartRequested() then
  begin
    // Derived half: HKCU Run value. Bare exe path on purpose -- the app's own
    // AutoStartHelper writes exactly this form, and the app-side merge only
    // checks presence; keep one canonical shape.
    RegWriteStringValue(HKCU, RunKeyPath, RunValueName, ExpandConstant('{app}\{#MyAppExeName}'));
    // SSOT merge signal: pending flag next to settings.json. The app merges
    // settings.json on first launch; this installer NEVER edits that file.
    FlagDir := ExpandConstant(PendingFlagDir);
    ForceDirectories(FlagDir);
    SaveStringToFile(FlagDir + '\' + PendingFlagFile, 'pending', False);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  case CurUninstallStep of
    usUninstall:
      // The Run value is app-owned and derived; remove it however it was written
      // (task checkbox, /TASKS=autostart, or /AUTOSTART=1).
      RegDeleteValue(HKCU, RunKeyPath, RunValueName);
    usPostUninstall:
      if not UninstallSilent then
        MsgBox(
          'Downloads kept: %LOCALAPPDATA%\WinLocalASR still holds the speech models ' +
          '(about 2.5 GB) and engine binaries. Delete that folder manually to reclaim ' +
          'the space if you do not plan to reinstall.',
          mbInformation, MB_OK);
  end;
end;
