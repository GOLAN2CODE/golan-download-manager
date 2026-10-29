; Golan Download Manager (GDM) — setup wizard (Inno Setup 6).
; Build: publish a self-contained single-file GDM.exe into installer\build, then run ISCC.exe gdm.iss
; (GDM\installer\build-setup.ps1 does both). Per-user install, no administrator rights.

#define AppName "Golan"
#define AppVersion "2.5.0"
#define AppExe "GDM.exe"

[Setup]
AppId={{8C4E9A12-7B3D-4F50-A61E-90B1A4D22001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={cm:AppTitle} {#AppVersion}
AppPublisher=Golan
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Golan Download Manager
DefaultDirName={localappdata}\Programs\Golan\GDM
DisableProgramGroupPage=yes
DefaultGroupName={cm:AppGroup}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\..
OutputBaseFilename=GDMSetup
SetupIconFile=..\src\GDM\Assets\gdm.ico
WizardImageFile=wizard-large-100.bmp,wizard-large-125.bmp,wizard-large-150.bmp,wizard-large-175.bmp,wizard-large-200.bmp,wizard-large-225.bmp,wizard-large-250.bmp
WizardSmallImageFile=wizard-small-100.bmp,wizard-small-125.bmp,wizard-small-150.bmp,wizard-small-175.bmp,wizard-small-200.bmp,wizard-small-225.bmp,wizard-small-250.bmp
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={cm:AppTitle}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
CloseApplications=no
RestartApplications=no

[Languages]
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ar.AppTitle=جولان - مدير التنزيل
en.AppTitle=Golan Download Manager
ar.AppGroup=جولان
en.AppGroup=Golan
ar.DesktopIcon=إنشاء اختصار على سطح المكتب
en.DesktopIcon=Create a desktop shortcut
ar.StartWithWindows=تشغيل جولان مع بدء تشغيل ويندوز
en.StartWithWindows=Start Golan when Windows starts
ar.OtherTasks=خيارات إضافية:
en.OtherTasks=Additional options:
ar.LaunchApp=تشغيل جولان الآن
en.LaunchApp=Launch Golan now
ar.ClosingApp=سيُغلَق برنامج جولان المفتوح حاليًا لإكمال العملية. هل تريد المتابعة؟
en.ClosingApp=Golan is running and will be closed to continue. Continue?
ar.DeleteData=هل تريد أيضًا حذف إعدادات جولان وقائمة التنزيلات؟%n(الملفات التي نزّلتها لن تُحذف.)
en.DeleteData=Also delete Golan's settings and download list?%n(Your downloaded files will not be deleted.)

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:OtherTasks}"
Name: "startup"; Description: "{cm:StartWithWindows}"; GroupDescription: "{cm:OtherTasks}"

[Files]
Source: "build\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{cm:AppGroup}\{cm:AppTitle}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{cm:AppTitle}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Golan.GDM"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[InstallDelete]
; Shortcuts left by built-in installer.
Type: files; Name: "{autoprograms}\Golan.lnk"
Type: files; Name: "{autodesktop}\Golan.lnk"

[UninstallDelete]
Type: files; Name: "{app}\{#AppExe}.bak"
Type: dirifempty; Name: "{app}"
Type: dirifempty; Name: "{localappdata}\Programs\Golan"

[Code]
const
  NativeHost = '\NativeMessagingHosts\com.golan.gdm';

function ProcessIsRunning(ExeName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/c tasklist /FI "IMAGENAME eq ' + ExeName + '" /NH | find /I "' + ExeName + '" >nul',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function AppIsRunning(): Boolean;
begin
  Result := ProcessIsRunning('GDM.exe') or ProcessIsRunning('Golan.exe');
end;

procedure CloseAppProcesses();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM GDM.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Golan.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if AppIsRunning() then
  begin
    if WizardSilent() or (SuppressibleMsgBox(CustomMessage('ClosingApp'), mbConfirmation, MB_YESNO, IDYES) = IDYES) then
      CloseAppProcesses()
    else
    begin
      Result := CustomMessage('ClosingApp');
      Exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  SettingsFile: String;
begin
  // The built-in installer registers its own uninstall entry; the wizard's entry replaces it.
  if CurStep = ssInstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Golan.GDM');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Golan');
  end;
  // A fresh install in Arabic starts Golan in Arabic; existing settings are never touched.
  if (CurStep = ssPostInstall) and (ActiveLanguage() = 'ar') then
  begin
    SettingsFile := ExpandConstant('{userappdata}\Golan\GDM\settings.json');
    if not FileExists(SettingsFile) then
    begin
      ForceDirectories(ExtractFileDir(SettingsFile));
      SaveStringToFile(SettingsFile, '{ "Language": "ar" }', False);
    end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if AppIsRunning() then
  begin
    if SuppressibleMsgBox(CustomMessage('ClosingApp'), mbConfirmation, MB_YESNO, IDYES) = IDYES then
      CloseAppProcesses()
    else
      Result := False;
  end;
end;

procedure RemoveHost(Root: String);
begin
  RegDeleteKeyIncludingSubkeys(HKCU, Root + NativeHost);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cmd: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveHost('Software\Google\Chrome');
    RemoveHost('Software\Microsoft\Edge');
    RemoveHost('Software\Mozilla');
    RemoveHost('Software\BraveSoftware\Brave-Browser');
    RemoveHost('Software\Chromium');
    RemoveHost('Software\Opera Software');
    RemoveHost('Software\Vivaldi');
    // Start-with-Windows may also have been switched on from Golan's options.
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Golan.GDM');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Golan');
    // magnet: links — only when they still open GDM/Golan.
    if RegQueryStringValue(HKCU, 'Software\Classes\magnet\shell\open\command', '', Cmd) and
       ((Pos('gdm.exe', Lowercase(Cmd)) > 0) or (Pos('golan.exe', Lowercase(Cmd)) > 0)) then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\magnet');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent() and (SuppressibleMsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
    begin
      DelTree(ExpandConstant('{userappdata}\Golan\GDM'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Golan\GDM'), True, True, True);
    end;
  end;
end;
