#ifndef PayloadDir
  #error PayloadDir must point to the prepared payload
#endif
#ifndef OutputDir
  #error OutputDir must point to the build output directory
#endif
#ifndef AppVersion
  #error AppVersion must match the application build
#endif
[Setup]
AppId={{DD9B4565-59BD-4CA6-8B98-A53AFEFDDC81}
AppName=RoboCopyUI
AppVersion={#AppVersion}
AppVerName=RoboCopyUI {#AppVersion}
DefaultDirName={localappdata}\Programs\RoboCopyUI
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=RoboCopyUI-{#AppVersion}-win-x64-setup
SetupIconFile=..\Assets\app_icon.ico
UninstallDisplayIcon={app}\RoboCopyUI.exe
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AppMutex={code:RunningMutex}
UninstallLogMode=append

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[LangOptions]
chinesesimplified.DialogFontName=Microsoft YaHei UI
chinesesimplified.WelcomeFontName=Microsoft YaHei UI

[CustomMessages]
chinesesimplified.OpenSettings=打开 RoboCopyUI 设置
english.OpenSettings=Open RoboCopyUI settings
chinesesimplified.InvalidPath=安装位置必须是本用户的标准 RoboCopyUI 目录，且不能包含链接或重解析点。
english.InvalidPath=Use the standard RoboCopyUI folder for this user. Links and reparse points are not allowed.
chinesesimplified.IntegrationFailed=程序文件已安装，但部分桌面集成尚未完成。请查看安装目录 integration.log，修复安装后从设置核对登录启动。
english.IntegrationFailed=Application files were installed, but desktop integration did not finish. Check integration.log in the installation folder, repair the installation, then check sign-in startup in settings.
chinesesimplified.InvalidUninstall=卸载目录校验失败。请先修复安装，未清理任何文件。
english.InvalidUninstall=The uninstall folder could not be verified. Repair the installation first. No files were cleaned up.
chinesesimplified.PurgePrompt=可选清理：是否同时移除本用户的应用设置、任务请求及任务日志？默认选择“否”以保留。未知文件会保留，复制来源和目标不会作为卸载清理对象。
english.PurgePrompt=Optional cleanup: also remove this user's application settings, task requests and task logs? No is selected by default to retain them. Unknown files are retained. Copy sources and destinations are never uninstall cleanup targets.
chinesesimplified.UninstallFailed=桌面集成或可选数据清理未完成。已停止卸载，请查看 integration.log；必要时先修复安装。
english.UninstallFailed=Desktop integration removal or optional data cleanup did not finish. Uninstall was stopped. Check integration.log and repair the installation if necessary.

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\RoboCopyUI"; Filename: "{app}\RoboCopyUI.exe"; Parameters: "--settings"; AppUserModelID: "RoboCopyUI.Desktop"

[Run]
Filename: "{app}\RoboCopyUI.exe"; Parameters: "--settings"; Description: "{cm:OpenSettings}"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\integration.log"

[Code]
var PurgeData: Boolean;

function RunningMutex(Param: String): String;
begin
  Result := 'Global\RoboCopyUI.Running.' + GetEnv('USERDOMAIN') + '.' + GetUserNameString;
end;

function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function SafeInstallPath: Boolean;
var Part, Parent: String; Attributes: Cardinal;
begin
  Result := False;
  Part := ExpandConstant('{app}');
  if CompareText(RemoveBackslashUnlessRoot(Part), ExpandConstant('{localappdata}\Programs\RoboCopyUI')) <> 0 then exit;
  while Length(Part) > 2 do begin
    Attributes := GetFileAttributesW(Part);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then exit;
    Parent := ExtractFileDir(Part);
    if Parent = Part then break;
    Part := Parent;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not SafeInstallPath then Result := CustomMessage('InvalidPath');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer;
begin
  if CurStep = ssPostInstall then begin
    if not Exec(ExpandConstant('{app}\RoboCopyUI.exe'), '--configure-installation', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then begin
      Log('Desktop integration was not completely configured. See integration.log.');
      if not WizardSilent then MsgBox(CustomMessage('IntegrationFailed'), mbInformation, MB_OK);
    end;
  end;
end;

function InitializeUninstall: Boolean;
begin
  PurgeData := False;
  Result := SafeInstallPath;
  if not Result then begin MsgBox(CustomMessage('InvalidUninstall'), mbError, MB_OK); exit; end;
  if not UninstallSilent then
    PurgeData := MsgBox(CustomMessage('PurgePrompt'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Code: Integer; Arguments: String;
begin
  if CurUninstallStep = usUninstall then begin
    Arguments := '--remove-installation-integration';
    if PurgeData then Arguments := Arguments + ' --purge-user-data';
    if not Exec(ExpandConstant('{app}\RoboCopyUI.exe'), Arguments, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException(CustomMessage('UninstallFailed'));
  end;
end;
