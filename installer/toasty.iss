; Toasty installer (Inno Setup 6). Build with build-installer.ps1, which passes AppVersion
; and makes sure the published exe, the PawnIO redistributable and wizard images exist.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define PawnIOVersion "2.2.0"

[Setup]
AppId={{6F2B7C1E-4A3D-4E8B-9C51-7A2D0E3F9B14}
AppName=Toasty
AppVersion={#AppVersion}
AppVerName=Toasty {#AppVersion}
AppPublisher=BRDN
AppPublisherURL=https://github.com/brundyy/toasty
AppSupportURL=https://github.com/brundyy/toasty/issues
AppUpdatesURL=https://github.com/brundyy/toasty/releases
AppCopyright=Copyright (c) 2026 BRDN. MIT licence.
VersionInfoDescription=Toasty setup
; Plain-English page listing everything that gets installed, and why (shown before installing).
InfoBeforeFile=before-install.rtf
DefaultDirName={autopf}\Toasty
DefaultGroupName=Toasty
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Toasty-Setup-{#AppVersion}
SetupIconFile=..\assets\toasty.ico
UninstallDisplayIcon={app}\Toasty.exe
UninstallDisplayName=Toasty
WizardStyle=modern
WizardImageFile=build\wizard-large.bmp
WizardSmallImageFile=build\wizard-small.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=no

[Tasks]
; The driver is opt-out and named, rather than installed silently. Only offered when needed.
Name: "pawnio"; Description: "Install the PawnIO driver (needed for CPU temperature; by namazso, signed, GPL-2.0)"; GroupDescription: "Hardware access:"; Check: not PawnIOUpToDate
Name: "autostart"; Description: "Start Toasty when I sign in"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\Toasty.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "redist\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "redist\PresentMon.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
#ifexist "..\LICENSE"
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\Toasty"; Filename: "{app}\Toasty.exe"
Name: "{group}\Uninstall Toasty"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Toasty"; Filename: "{app}\Toasty.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Toasty.exe"; Parameters: "--autostart on"; Flags: runhidden waituntilterminated; Tasks: autostart; StatusMsg: "Setting up startup task..."
; postinstall entries run as the original (non-elevated) user by default, which can't start an
; admin-manifested exe (error 740). runascurrentuser launches it with Setup's elevation instead.
Filename: "{app}\Toasty.exe"; Description: "Launch Toasty now"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
Filename: "{app}\Toasty.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitToasty"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Toasty.exe /F"; Flags: runhidden; RunOnceId: "KillToasty"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -Command ""Get-Process PresentMon -ErrorAction SilentlyContinue | Where-Object Path -eq '{app}\PresentMon.exe' | Stop-Process -Force"""; Flags: runhidden; RunOnceId: "KillPresentMon"
Filename: "{app}\Toasty.exe"; Parameters: "--autostart off"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutostart"

[Code]
var
  PawnIONeedsRestart: Boolean;

function PawnIOInstalledVersion(): String;
begin
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayVersion', Result) then
    Result := '';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  App: String;
begin
  App := ExpandConstant('{app}');
  // Ask a running Toasty to exit cleanly first (saves any game session in progress), then
  // force-close whatever is left so its files can be replaced.
  if FileExists(App + '\Toasty.exe') then
    Exec(App + '\Toasty.exe', '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Toasty.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // Older versions could leave PresentMon running behind them; stop only Toasty's own copy.
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "Get-Process PresentMon -ErrorAction SilentlyContinue | Where-Object Path -eq ''' + App + '\PresentMon.exe'' | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

// True when PawnIO is installed at the bundled version or newer. The registry reports
// "2.2.0.0" while we pin "2.2.0", so compare as versions rather than strings.
function PawnIOUpToDate(): Boolean;
var
  Installed, Bundled: Int64;
begin
  Result := StrToVersion(PawnIOInstalledVersion(), Installed)
        and StrToVersion('{#PawnIOVersion}', Bundled)
        and (ComparePackedVersion(Installed, Bundled) >= 0);
end;

procedure InstallPawnIO();
var
  Code: Integer;
begin
  // PawnIO is the signed kernel driver LibreHardwareMonitor uses for CPU temperatures.
  if PawnIOUpToDate() or not WizardIsTaskSelected('pawnio') then Exit;

  WizardForm.StatusLabel.Caption := 'Installing the PawnIO hardware driver...';
  if Exec(ExpandConstant('{tmp}\PawnIO_setup.exe'), '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED, 183 = ERROR_ALREADY_EXISTS (already installed)
    if Code = 3010 then
      PawnIONeedsRestart := True
    else if (Code <> 0) and (Code <> 183) then
      MsgBox('The PawnIO driver could not be installed (code ' + IntToStr(Code) + ').' + #13#10 +
             'Toasty will still run, but CPU temperature will be unavailable.', mbInformation, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallPawnIO();
end;

function NeedRestart(): Boolean;
begin
  Result := PawnIONeedsRestart;
end;

// The "Ready to install" summary: Windows' usual details plus a plain list of what's going on the PC.
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
var
  Driver: String;
begin
  if PawnIOUpToDate() then
    Driver := 'PawnIO driver ' + PawnIOInstalledVersion() + ' (already installed, left as is)'
  else if WizardIsTaskSelected('pawnio') then
    Driver := 'PawnIO {#PawnIOVersion} hardware driver by namazso (GPL-2.0), installed system-wide'
  else
    Driver := 'PawnIO driver: skipped (CPU temperature will be unavailable)';

  Result := 'What will be installed:' + NewLine +
    Space + 'Toasty {#AppVersion} (MIT), with the .NET runtime built in (Microsoft, MIT)' + NewLine +
    Space + 'PresentMon 2.6.0 by Intel (MIT), used only to measure FPS in games' + NewLine +
    Space + 'LibreHardwareMonitor sensor library (MPL-2.0), built into Toasty' + NewLine +
    Space + Driver + NewLine +
    Space + 'Licences and third-party notices, in the install folder' + NewLine + NewLine +
    'Toasty never connects to the internet. Settings and game sessions stay on this PC.' + NewLine;
  if MemoDirInfo <> '' then Result := Result + NewLine + MemoDirInfo + NewLine;
  if MemoTasksInfo <> '' then Result := Result + NewLine + MemoTasksInfo + NewLine;
end;
