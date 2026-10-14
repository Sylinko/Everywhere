#define AppName "Everywhere"
#define AppPublisher "Sylinko"
#define AppExeName "Everywhere.exe"
#define AppVersion GetEnv("VERSION")

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={code:GetDefaultInstallPath}
DefaultGroupName={#AppName}
OutputDir=..
OutputBaseFilename=Everywhere-Windows-x64-Setup-v{#AppVersion}
PrivilegesRequired=admin
UsePreviousAppDir=no
DisableDirPage=no
DirExistsWarning=no
AlwaysShowDirOnReadyPage=yes
AllowUNCPath=no
AllowNetworkDrive=no
SetupMutex=Global\Everywhere.Setup.D66EA41B-8DEB-4E5A-9D32-AB4F8305F664
Compression=lzma2
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallLogging=yes
RedirectionGuard=yes
SetupArchitecture=x64
#ifndef SkipInstallerSigning
SignTool=EverywhereSign
SignedUninstaller=yes
SignToolRetryCount=3
SignToolRetryDelay=2000
#endif
WizardStyle=modern dynamic
SetupIconFile=..\img\Everywhere.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
; Preserve the released AppId, including its second closing brace.
AppId={{D66EA41B-8DEB-4E5A-9D32-AB4F8305F664}}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
en.InstallDirectoryNotEmpty=This folder is not empty. Are you sure you want to continue installing?
zh.InstallDirectoryNotEmpty=文件夹不是空的，你确定要继续安装吗？
en.ClosingEverywhere=Closing Everywhere...
zh.ClosingEverywhere=正在关闭 Everywhere…
en.RemovingPreviousVersion=Removing the previous version...
zh.RemovingPreviousVersion=正在移除旧版本…
en.PreviousUninstallFailed=The previous version could not be fully uninstalled (error code %1). Choose Retry to try again, Ignore to continue installing, or Abort to stop Setup. Choosing Ignore may leave files from the previous version. See the Setup log for details.
zh.PreviousUninstallFailed=旧版本未能完全卸载（错误代码 %1）。点击“重试”再次尝试，点击“忽略”继续安装，或点击“中止”退出安装。选择“忽略”可能留下旧版本的文件。详情请查看安装日志。
en.PreviousUninstallWaitFailed=Setup could not confirm whether the previous version has finished uninstalling (error code %1). Choose Retry to keep waiting, Ignore to continue installing, or Abort to stop Setup. If you choose Ignore, the previous uninstaller may still be running.
zh.PreviousUninstallWaitFailed=无法确认旧版本是否已卸载完成（错误代码 %1）。点击“重试”继续等待，点击“忽略”继续安装，或点击“中止”退出安装。选择“忽略”时，旧卸载程序可能仍在运行。
en.InstallWillCloseEverywhere=Everywhere will close when installation starts.
zh.InstallWillCloseEverywhere=安装开始时将关闭 Everywhere。
en.PreviousInstallationInUse=Everywhere is still running. Please exit Everywhere, then choose Retry. Choose Continue to try to force it to close and continue installing. Unsaved changes may be lost. Choose Cancel to stop Setup.
zh.PreviousInstallationInUse=Everywhere 仍在运行。请先退出 Everywhere，再点击“重试”。点击“继续”将尝试强制关闭 Everywhere 并继续安装，未保存的内容可能丢失。点击“取消”退出安装。
en.RetryExit=Retry
zh.RetryExit=重试
en.ContinueExit=Continue
zh.ContinueExit=继续
en.CancelExit=Cancel
zh.CancelExit=取消
en.WaitingForExit=Waiting for Everywhere to exit...
zh.WaitingForExit=正在等待 Everywhere 自行退出…
en.WaitingForExitDetail=Waiting up to 10 seconds.
zh.WaitingForExitDetail=最多等待 10 秒。
en.PreviousInstallationCheckFailed=Setup could not check whether Everywhere is still running (Windows error %1). Please exit Everywhere, then choose Retry. Choose Continue to try to force it to close and continue installing. Unsaved changes may be lost. Choose Cancel to stop Setup.
zh.PreviousInstallationCheckFailed=安装程序无法确认 Everywhere 是否仍在运行（Windows 错误 %1）。请先退出 Everywhere，再点击“重试”。点击“继续”将尝试强制关闭 Everywhere 并继续安装，未保存的内容可能丢失。点击“取消”退出安装。
en.ShortcutCreationFailed=Everywhere was installed, but some shortcuts could not be created. You can still open Everywhere from its installation folder. See the Setup log for details.
zh.ShortcutCreationFailed=Everywhere 已安装，但有些快捷方式未能创建。您仍可从安装文件夹打开 Everywhere。详情请查看安装日志。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
Name: "{app}"; Flags: uninsalwaysuninstall

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
const
  InstallRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D66EA41B-8DEB-4E5A-9D32-AB4F8305F664}}_is1';
  LegacyRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Everywhere';
  ShutdownTimeout = 10000;
  WaitTimeout = 258;
  CreateSuspended = $4;
  CreateUnicodeEnvironment = $400;
  CreateNoWindow = $08000000;

type
  TPreviousInstallation = record
    RootKey: HKEY;
    Key: String;
    Directory: String;
    Uninstaller: String;
    LayoutVersion: Cardinal;
    ShutdownProtocolVersion: Cardinal;
    IsDesktopUser: Boolean;
    HasRemoved: Boolean;
  end;
  TProcessIds = array[0..4095] of Cardinal;
  TRunningProcess = record
    Id: Cardinal;
    Path: String;
  end;
  TRunningProcesses = array of TRunningProcess;
  { These records explicitly include the padding of the native x64 ABI. }
  TNativeStartupInfo = record
    Size: Cardinal;
    Padding: Cardinal;
    Reserved: NativeInt;
    Desktop: NativeInt;
    Title: NativeInt;
    X: Cardinal;
    Y: Cardinal;
    XSize: Cardinal;
    YSize: Cardinal;
    XCountChars: Cardinal;
    YCountChars: Cardinal;
    FillAttribute: Cardinal;
    Flags: Cardinal;
    ShowWindow: Word;
    ReservedSize: Word;
    Padding2: Cardinal;
    Reserved2: NativeInt;
    StdInput: NativeInt;
    StdOutput: NativeInt;
    StdError: NativeInt;
  end;
  TNativeProcessInfo = record
    Process: NativeInt;
    Thread: NativeInt;
    ProcessId: Cardinal;
    ThreadId: Cardinal;
  end;
  TNativeMessage = record
    Window: NativeInt;
    Message: Cardinal;
    Padding: Cardinal;
    WParam: NativeInt;
    LParam: NativeInt;
    Time: Cardinal;
    X: LongInt;
    Y: LongInt;
    PrivateData: Cardinal;
  end;
  TNativeJobLimits = record
    ProcessTime: Int64;
    JobTime: Int64;
    Flags: Cardinal;
    Padding: Cardinal;
    MinimumWorkingSet: NativeInt;
    MaximumWorkingSet: NativeInt;
    ActiveProcessLimit: Cardinal;
    Padding2: Cardinal;
    Affinity: NativeInt;
    Priority: Cardinal;
    SchedulingClass: Cardinal;
    IoCounters: array[0..5] of Int64;
    MemoryLimits: array[0..3] of NativeInt;
  end;
  TNativeTokenUser = record
    Sid: NativeInt;
    Attributes: Cardinal;
    Padding: Cardinal;
    SidBuffer: array[0..67] of Byte;
  end;

var
  PreviousInstallations: array of TPreviousInstallation;
  DesktopUserRoot: HKEY;
  DesktopUserLocalAppData: String;
  DeviceIdPath: String;
  DeviceIdContents: AnsiString;
  HasCapturedDeviceId: Boolean;
  HasSavedDeviceId: Boolean;
  NeedsDeviceIdRestore: Boolean;

  DesktopUserToken: NativeInt;
  DesktopUserHive: String;
  DesktopUserDesktop: String;
  DesktopUserPrograms: String;
  ActiveControllerJob: NativeInt;
  IsPreparingInstallation: Boolean;
  HasPreparedInstallation: Boolean;
  HasCapturedPreviousDesktopShortcuts: Boolean;
  PreviousCommonDesktopTarget: String;
  PreviousUserDesktopTarget: String;
  LaunchCheckBox: TNewCheckBox;

function WindowsGetTickCount64(): Int64;
  external 'GetTickCount64@kernel32.dll stdcall';
function WindowsCloseHandle(Handle: NativeInt): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
procedure WindowsExitProcess(ExitCode: Cardinal);
  external 'ExitProcess@kernel32.dll stdcall';
function WindowsGetShellWindow(): NativeInt;
  external 'GetShellWindow@user32.dll stdcall';
function WindowsGetWindowThreadProcessId(Window: NativeInt; var ProcessId: Cardinal): Cardinal;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function WindowsOpenProcess(Access: Cardinal; InheritHandle: BOOL; ProcessId: Cardinal): NativeInt;
  external 'OpenProcess@kernel32.dll stdcall';
function WindowsOpenProcessToken(Process: NativeInt; Access: Cardinal; var Token: NativeInt): BOOL;
  external 'OpenProcessToken@advapi32.dll stdcall';
function WindowsDuplicateTokenEx(Token: NativeInt; Access: Cardinal; Attributes: NativeInt; Level: Cardinal; TokenType: Cardinal; var NewToken: NativeInt): BOOL;
  external 'DuplicateTokenEx@advapi32.dll stdcall';
function WindowsGetTokenUser(Token: NativeInt; InformationClass: Cardinal; var User: TNativeTokenUser; Size: Cardinal; var ReturnedSize: Cardinal): BOOL;
  external 'GetTokenInformation@advapi32.dll stdcall';
function WindowsConvertSidToStringSid(Sid: NativeInt; var StringSid: NativeInt): BOOL;
  external 'ConvertSidToStringSidW@advapi32.dll stdcall';
function WindowsCopyString(Destination: String; Source: NativeInt; Size: Integer): NativeInt;
  external 'lstrcpynW@kernel32.dll stdcall';
function WindowsCreateProcess(ApplicationName: String; CommandLine: String; ProcessAttributes: NativeInt; ThreadAttributes: NativeInt; InheritHandles: BOOL; Flags: Cardinal; Environment: NativeInt; Directory: String; var Startup: TNativeStartupInfo; var ProcessInfo: TNativeProcessInfo): BOOL;
  external 'CreateProcessW@kernel32.dll stdcall';
function WindowsCreateProcessWithToken(Token: NativeInt; LogonFlags: Cardinal; ApplicationName: String; CommandLine: String; Flags: Cardinal; Environment: NativeInt; Directory: String; var Startup: TNativeStartupInfo; var ProcessInfo: TNativeProcessInfo): BOOL;
  external 'CreateProcessWithTokenW@advapi32.dll stdcall';
function WindowsCreateEnvironmentBlock(var Environment: NativeInt; Token: NativeInt; Inherit: BOOL): BOOL;
  external 'CreateEnvironmentBlock@userenv.dll stdcall';
function WindowsDestroyEnvironmentBlock(Environment: NativeInt): BOOL;
  external 'DestroyEnvironmentBlock@userenv.dll stdcall';
function WindowsCreateJobObject(Attributes: NativeInt; Name: NativeInt): NativeInt;
  external 'CreateJobObjectW@kernel32.dll stdcall';
function WindowsSetJobLimits(Job: NativeInt; InformationClass: Cardinal; var Limits: TNativeJobLimits; Size: Cardinal): BOOL;
  external 'SetInformationJobObject@kernel32.dll stdcall';
function WindowsAssignProcessToJob(Job: NativeInt; Process: NativeInt): BOOL;
  external 'AssignProcessToJobObject@kernel32.dll stdcall';
function WindowsTerminateProcess(Process: NativeInt; ExitCode: Cardinal): BOOL;
  external 'TerminateProcess@kernel32.dll stdcall';
function WindowsResumeThread(Thread: NativeInt): Cardinal;
  external 'ResumeThread@kernel32.dll stdcall';
function WindowsWaitForSingleObject(Handle: NativeInt; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function WindowsGetExitCodeProcess(Process: NativeInt; var ExitCode: Cardinal): BOOL;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function WindowsPeekMessage(var Message: TNativeMessage; Window: NativeInt; MinMessage: Cardinal; MaxMessage: Cardinal; Remove: Cardinal): BOOL;
  external 'PeekMessageW@user32.dll stdcall';
function WindowsTranslateMessage(var Message: TNativeMessage): BOOL;
  external 'TranslateMessage@user32.dll stdcall';
function WindowsDispatchMessage(var Message: TNativeMessage): NativeInt;
  external 'DispatchMessageW@user32.dll stdcall';
function WindowsLocalFree(Memory: NativeInt): NativeInt;
  external 'LocalFree@kernel32.dll stdcall';
function WindowsEnumProcesses(var Ids: TProcessIds; Size: Cardinal; var BytesNeeded: Cardinal): BOOL;
  external 'K32EnumProcesses@kernel32.dll stdcall';
function WindowsQueryProcessPath(Process: NativeInt; Flags: Cardinal; Path: String; var Size: Cardinal): BOOL;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function WindowsGetFileAttributes(Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsSameDirectory(FirstPath: String; SecondPath: String): Boolean;
begin
  Result := (FirstPath <> '') and (SecondPath <> '') and
    (CompareText(RemoveBackslashUnlessRoot(ExpandFileName(FirstPath)), RemoveBackslashUnlessRoot(ExpandFileName(SecondPath))) = 0);
end;

function IsSameFile(FirstPath: String; SecondPath: String): Boolean;
begin
  Result := (FirstPath <> '') and (SecondPath <> '') and
    (CompareText(ExpandFileName(RemoveQuotes(FirstPath)), ExpandFileName(RemoveQuotes(SecondPath))) = 0);
end;

function ExtractCommandExecutable(CommandLine: String): String;
var
  ClosingQuote: Integer;
  Separator: Integer;
begin
  Result := '';
  CommandLine := Trim(CommandLine);
  if CommandLine = '' then
    exit;
  if CompareText(Copy(CommandLine, Length(CommandLine) - 3, 4), '.exe') = 0 then
  begin
    Result := CommandLine;
    exit;
  end;
  if CommandLine[1] = '"' then
  begin
    ClosingQuote := Pos('"', Copy(CommandLine, 2, MaxInt));
    if ClosingQuote > 0 then
      Result := Copy(CommandLine, 2, ClosingQuote - 1);
    exit;
  end;
  Separator := Pos(' ', CommandLine);
  if Separator = 0 then
    Result := CommandLine
  else
    Result := Copy(CommandLine, 1, Separator - 1);
end;

function HasCommandLineParameter(Parameter: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), Parameter) = 0 then
    begin
      Result := True;
      exit;
    end;
end;

procedure CaptureDesktopUser();
var
  ProcessId: Cardinal;
  Process: NativeInt;
  Token: NativeInt;
  User: TNativeTokenUser;
  ReturnedSize: Cardinal;
  StringSid: NativeInt;
  Sid: String;
begin
  { GetShellWindow belongs to this desktop session, including alternate-credential
    elevation. Keep its token rather than guessing the user from elevated HKCU. }
  if WindowsGetWindowThreadProcessId(WindowsGetShellWindow(), ProcessId) = 0 then
    exit;
  Process := WindowsOpenProcess($1000, False, ProcessId);
  if Process = 0 then
    exit;
  try
    if not WindowsOpenProcessToken(Process, $A, Token) then
      exit;
    try
      { CreateProcessWithTokenW also needs ADJUST_DEFAULT and ADJUST_SESSIONID
        on the duplicated primary token, in addition to QUERY/DUPLICATE/ASSIGN. }
      if not WindowsDuplicateTokenEx(Token, $18B, 0, 2, 1, DesktopUserToken) then
      begin
        Log(Format('Could not duplicate the desktop user token: Windows error %d.', [DLLGetLastError]));
        exit;
      end;
      if not WindowsGetTokenUser(Token, 1, User, SizeOf(User), ReturnedSize) then
        exit;
      if not WindowsConvertSidToStringSid(User.Sid, StringSid) then
        exit;
      try
        Sid := StringOfChar(#0, 184);
        WindowsCopyString(Sid, StringSid, Length(Sid));
        DesktopUserHive := Copy(Sid, 1, Pos(#0, Sid) - 1) + '\';
      finally
        WindowsLocalFree(StringSid);
      end;
    finally
      WindowsCloseHandle(Token);
    end;
  finally
    WindowsCloseHandle(Process);
  end;
  if DesktopUserHive <> '' then
  begin
    RegQueryStringValue(HKEY_USERS, DesktopUserHive + 'Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders', 'Desktop', DesktopUserDesktop);
    RegQueryStringValue(HKEY_USERS, DesktopUserHive + 'Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders', 'Programs', DesktopUserPrograms);
  end;
end;

function QueryPreviousInstall(RootKey: HKEY; Key: String; var InstallDirectory: String; var Uninstaller: String): Boolean;
var
  HasInstallDirectory: Boolean;
  HasUninstaller: Boolean;
begin
  HasInstallDirectory := RegQueryStringValue(RootKey, Key, 'InstallLocation', InstallDirectory);
  HasUninstaller := RegQueryStringValue(RootKey, Key, 'UninstallString', Uninstaller);
  if not HasInstallDirectory then
    RegQueryStringValue(RootKey, Key, 'Inno Setup: App Path', InstallDirectory);
  if InstallDirectory = '' then
    InstallDirectory := ExtractFileDir(ExtractCommandExecutable(Uninstaller));
  Result := (InstallDirectory <> '') or HasUninstaller;
end;

procedure AddPreviousInstallation(RootKey: HKEY; Prefix: String; Key: String; IsDesktopUser: Boolean);
var
  Installation: TPreviousInstallation;
  Identifier: String;
  Index: Integer;
begin
  if Key = LegacyRegistryKey then
    if not RegQueryStringValue(RootKey, Prefix + Key, 'Identifier', Identifier) or
       (Identifier <> 'D66EA41B-8DEB-4E5A-9D32-AB4F8305F664') then
      exit;
  if not QueryPreviousInstall(RootKey, Prefix + Key, Installation.Directory, Installation.Uninstaller) then
    exit;
  Installation.RootKey := RootKey;
  Installation.Key := Prefix + Key;
  Installation.IsDesktopUser := IsDesktopUser;
  RegQueryDWordValue(RootKey, Installation.Key, 'InstallLayoutVersion', Installation.LayoutVersion);
  RegQueryDWordValue(RootKey, Installation.Key, 'ShutdownProtocolVersion', Installation.ShutdownProtocolVersion);
  Index := GetArrayLength(PreviousInstallations);
  SetArrayLength(PreviousInstallations, Index + 1);
  PreviousInstallations[Index] := Installation;
end;

function InitializeSetup(): Boolean;
begin
  CaptureDesktopUser();
  DesktopUserRoot := HKEY_CURRENT_USER;
  if DesktopUserHive <> '' then
    DesktopUserRoot := HKEY_USERS;
  RegQueryStringValue(DesktopUserRoot, DesktopUserHive + 'Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders', 'Local AppData', DesktopUserLocalAppData);
  if DesktopUserHive = '' then
  begin
    DesktopUserLocalAppData := ExpandConstant('{localappdata}');
    DesktopUserDesktop := ExpandConstant('{userdesktop}');
    DesktopUserPrograms := ExpandConstant('{userprograms}');
    Log('The desktop user could not be resolved; using the current account for installation discovery.');
  end;
  AddPreviousInstallation(HKEY_LOCAL_MACHINE, '', InstallRegistryKey, False);
  AddPreviousInstallation(DesktopUserRoot, DesktopUserHive, InstallRegistryKey, True);
  AddPreviousInstallation(DesktopUserRoot, DesktopUserHive, LegacyRegistryKey, True);
  Result := True;
end;

function GetDefaultInstallPath(Param: String): String;
var
  Index: Integer;
begin
  Result := ExpandConstant('{autopf}\{#AppName}');
  for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
    if (PreviousInstallations[Index].LayoutVersion = 2) and (PreviousInstallations[Index].Directory <> '') then
    begin
      Result := RemoveBackslashUnlessRoot(PreviousInstallations[Index].Directory);
      exit;
    end;
end;

function ShouldSkipPage(PageId: Integer): Boolean;
var
  Index: Integer;
begin
  Result := (PageId = wpSelectDir) and (GetArrayLength(PreviousInstallations) > 0);
  for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
    if PreviousInstallations[Index].LayoutVersion <> 2 then
      Result := False;
end;

procedure InitializeWizard();
begin
  LaunchCheckBox := TNewCheckBox.Create(WizardForm);
  LaunchCheckBox.Parent := WizardForm.FinishedPage;
  LaunchCheckBox.SetBounds(WizardForm.RunList.Left, WizardForm.RunList.Top, WizardForm.RunList.Width, ScaleY(24));
  LaunchCheckBox.Caption := ExpandConstant('{cm:LaunchProgram,{#AppName}}');
  LaunchCheckBox.Checked := True;
  LaunchCheckBox.Visible := False;
end;

procedure CurPageChanged(PageId: Integer);
begin
  if PageId = wpFinished then
    LaunchCheckBox.Visible := (DesktopUserToken <> 0) and (not WizardSilent) and (not WizardForm.YesRadio.Visible);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + MemoGroupInfo + NewLine + MemoTasksInfo;
  if GetArrayLength(PreviousInstallations) > 0 then
    Result := Result + NewLine + NewLine + ExpandConstant('{cm:InstallWillCloseEverywhere}');
end;

function IsDirectoryNotEmpty(DirectoryPath: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not FindFirst(AddBackslash(DirectoryPath) + '*', FindRec) then
    exit;
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Result := True;
        exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function NextButtonClick(PageId: Integer): Boolean;
var
  Index: Integer;
begin
  Result := True;
  if PageId <> wpSelectDir then
    exit;
  for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
    if IsSameDirectory(WizardDirValue, PreviousInstallations[Index].Directory) then
      exit;
  if not IsDirectoryNotEmpty(WizardDirValue) then
    exit;
  Result := SuppressibleMsgBox(ExpandConstant('{cm:InstallDirectoryNotEmpty}'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;

function StartProgram(FileName: String; Parameters: String; IsDesktopUser: Boolean; Flags: Cardinal; var ProcessInfo: TNativeProcessInfo; var ErrorCode: Integer): Boolean;
var
  Startup: TNativeStartupInfo;
  Environment: NativeInt;
begin
  Result := False;
  ErrorCode := 0;
  Startup.Size := SizeOf(Startup);
  Startup.Flags := 1;
  Startup.ShowWindow := SW_HIDE;
  if (Flags and CreateNoWindow) = 0 then
    Startup.ShowWindow := SW_SHOWNORMAL;
  if IsDesktopUser then
  begin
    if DesktopUserToken = 0 then
    begin
      ErrorCode := 1008; { ERROR_NO_TOKEN }
      Log('Could not start the program because the desktop user token is unavailable.');
      exit;
    end;
    if not WindowsCreateEnvironmentBlock(Environment, DesktopUserToken, False) then
    begin
      ErrorCode := DLLGetLastError;
      Log(Format('Could not create the desktop user environment: Windows error %d.', [ErrorCode]));
      exit;
    end;
    try
      Result := WindowsCreateProcessWithToken(DesktopUserToken, 0, FileName, '"' + FileName + '" ' + Parameters,
        Flags or CreateUnicodeEnvironment, Environment, ExtractFileDir(FileName), Startup, ProcessInfo);
      ErrorCode := DLLGetLastError;
    finally
      WindowsDestroyEnvironmentBlock(Environment);
    end;
  end
  else
  begin
    Result := WindowsCreateProcess(FileName, '"' + FileName + '" ' + Parameters, 0, 0, False,
      Flags, 0, ExtractFileDir(FileName), Startup, ProcessInfo);
    ErrorCode := DLLGetLastError;
  end;
  if Result then
    ErrorCode := 0
  else
    Log(Format('Could not start "%s": Windows error %d.', [FileName, ErrorCode]));
end;

procedure PumpMessages();
var
  Message: TNativeMessage;
begin
  while WindowsPeekMessage(Message, 0, 0, 0, 1) do
  begin
    WindowsTranslateMessage(Message);
    WindowsDispatchMessage(Message);
  end;
end;

function RunController(FileName: String; Parameters: String; IsDesktopUser: Boolean; TimeoutMilliseconds: Cardinal; var ResultCode: Integer): Boolean;
var
  ProcessInfo: TNativeProcessInfo;
  LaunchErrorCode: Integer;
  Limits: TNativeJobLimits;
  ExitCode: Cardinal;
  WaitResult: Cardinal;
  Deadline: Int64;
begin
  Result := False;
  ResultCode := -1;
  ActiveControllerJob := WindowsCreateJobObject(0, 0);
  if ActiveControllerJob = 0 then
    exit;
  try
    { Optional controllers have bounded waits. Start suspended so their children
      belong to the kill-on-close job before any controller code executes. }
    Limits.Flags := $2000;
    if not WindowsSetJobLimits(ActiveControllerJob, 9, Limits, SizeOf(Limits)) then
      exit;
    if not StartProgram(FileName, Parameters, IsDesktopUser, CreateSuspended or CreateNoWindow, ProcessInfo, LaunchErrorCode) then
    begin
      ResultCode := LaunchErrorCode;
      exit;
    end;
    try
      if not WindowsAssignProcessToJob(ActiveControllerJob, ProcessInfo.Process) then
      begin
        WindowsTerminateProcess(ProcessInfo.Process, 2);
        exit;
      end;
      if WindowsResumeThread(ProcessInfo.Thread) = $FFFFFFFF then
        exit;
      Deadline := WindowsGetTickCount64() + TimeoutMilliseconds;
      repeat
        WaitResult := WindowsWaitForSingleObject(ProcessInfo.Process, 50);
        PumpMessages();
        if (WaitResult = WaitTimeout) and (WindowsGetTickCount64() >= Deadline) then
        begin
          Log('The optional controller timed out; stopping its process tree.');
          exit;
        end;
      until WaitResult <> WaitTimeout;
      if WaitResult <> 0 then
        exit;
      if not WindowsGetExitCodeProcess(ProcessInfo.Process, ExitCode) then
        exit;
      ResultCode := ExitCode;
      Result := ResultCode = 0;
    finally
      WindowsCloseHandle(ProcessInfo.Thread);
      WindowsCloseHandle(ProcessInfo.Process);
    end;
  finally
    WindowsCloseHandle(ActiveControllerJob);
    ActiveControllerJob := 0;
  end;
end;

function ShouldRetryPreviousUninstall(ResultCode: Integer; IsWaiting: Boolean): Boolean;
var
  Prompt: String;
  Response: Integer;
begin
  Result := False;
  if HasCommandLineParameter('/IGNOREUNINSTALLFAILURE') then
    exit;
  if WizardSilent or HasCommandLineParameter('/SUPPRESSMSGBOXES') then
    WindowsExitProcess(7);
  if IsWaiting then
    Prompt := ExpandConstant('{cm:PreviousUninstallWaitFailed}')
  else
    Prompt := ExpandConstant('{cm:PreviousUninstallFailed}');
  Response := MsgBox(FmtMessage(Prompt, [IntToStr(ResultCode)]),
    mbError, MB_ABORTRETRYIGNORE or MB_DEFBUTTON1);
  if Response = IDABORT then
    WindowsExitProcess(2);
  Result := Response = IDRETRY;
end;

function ExecutePreviousUninstaller(FileName: String; Parameters: String; IsDesktopUser: Boolean;
  var ResultCode: Integer; var HasIgnoredWaitFailure: Boolean): Boolean;
var
  ProcessInfo: TNativeProcessInfo;
  WaitResult: Cardinal;
  ExitCode: Cardinal;
begin
  { Inno keeps its original process alive through the main removal phase.
    Its temporary copy may still finish cleanup afterwards. This path is
    only used for modern installs, without subsequent recursive legacy cleanup. }
  Result := False;
  ResultCode := -1;
  HasIgnoredWaitFailure := False;
  if not IsDesktopUser then
  begin
    Result := Exec(FileName, Parameters, ExtractFileDir(FileName), SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
    Result := Result and (ResultCode = 0);
    exit;
  end;
  { Preserve the captured desktop identity for a user-scoped old installation,
    including when Setup was elevated using another account's credentials. }
  if not StartProgram(FileName, Parameters, True, 0, ProcessInfo, ResultCode) then
    exit;
  try
    repeat
      repeat
        WaitResult := WindowsWaitForSingleObject(ProcessInfo.Process, 50);
        if (WaitResult <> 0) and (WaitResult <> WaitTimeout) then
          ResultCode := DLLGetLastError;
        PumpMessages();
      until WaitResult <> WaitTimeout;
      if WaitResult <> 0 then
      begin
        Log(Format('Could not wait for the previous uninstaller: %d.', [ResultCode]));
      end
      else if WindowsGetExitCodeProcess(ProcessInfo.Process, ExitCode) then
        break
      else
      begin
        ResultCode := DLLGetLastError;
        Log(Format('Could not read the previous uninstall result: %d.', [ResultCode]));
      end;
      { Retry observes the same process; never launch a second uninstaller while
        the first one's completion is unknown. Ignore is handled here once. }
      if not ShouldRetryPreviousUninstall(ResultCode, True) then
      begin
        HasIgnoredWaitFailure := True;
        exit;
      end;
    until False;
    ResultCode := ExitCode;
    Result := ResultCode = 0;
  finally
    WindowsCloseHandle(ProcessInfo.Thread);
    WindowsCloseHandle(ProcessInfo.Process);
  end;
end;

function RestoreDeviceId(): Boolean; forward;

procedure CancelButtonClick(PageId: Integer; var Cancel, Confirm: Boolean);
begin
  if not IsPreparingInstallation then
    exit;
  if not WizardForm.CancelButton.Enabled then
  begin
    Cancel := False;
    Confirm := False;
    exit;
  end;
  { Outside old uninstall, cancellation stops preparation without entering
    payload rollback. Optional controller children are stopped by their job. }
  if ActiveControllerJob <> 0 then
    WindowsCloseHandle(ActiveControllerJob);
  if NeedsDeviceIdRestore then
    RestoreDeviceId();
  WindowsExitProcess(2);
end;

function IsInstallationProcess(Path: String; Directory: String; IsEverything: Boolean): Boolean;
begin
  Result := False;
  if Directory = '' then
    exit;
  if IsEverything then
    Result := IsSameFile(Path, AddBackslash(Directory) + 'runtimes\win-x64\native\Everything.exe')
  else
    Result := IsSameFile(Path, AddBackslash(Directory) + '{#AppExeName}') or
      IsSameFile(Path, AddBackslash(Directory) + 'Everywhere.Windows.exe') or
      IsSameFile(Path, AddBackslash(Directory) + 'Everywhere.Watchdog.exe');
end;

function ReadProcessPath(Process: NativeInt): String;
var
  Size: Cardinal;
begin
  Size := 32768;
  Result := StringOfChar(#0, Size);
  if WindowsQueryProcessPath(Process, 0, Result, Size) then
    SetLength(Result, Size)
  else
    Result := '';
end;

function FindInstallationProcesses(Directory: String; IsEverything: Boolean; var Processes: TRunningProcesses): Boolean;
var
  Ids: TProcessIds;
  BytesNeeded: Cardinal;
  Index: Integer;
  Count: Integer;
  Process: NativeInt;
  Path: String;
begin
  SetArrayLength(Processes, 0);
  Result := WindowsEnumProcesses(Ids, SizeOf(Ids), BytesNeeded);
  if not Result then
    exit;
  if BytesNeeded = SizeOf(Ids) then
  begin
    Log('The process enumeration buffer is full; refusing an incomplete inspection.');
    Result := False;
    exit;
  end;
  for Index := 0 to Integer(BytesNeeded div 4) - 1 do
  begin
    Process := WindowsOpenProcess($1000, False, Ids[Index]);
    if Process <> 0 then
    begin
      Path := ReadProcessPath(Process);
      WindowsCloseHandle(Process);
      if IsInstallationProcess(Path, Directory, IsEverything) then
      begin
        Count := GetArrayLength(Processes);
        SetArrayLength(Processes, Count + 1);
        Processes[Count].Id := Ids[Index];
        Processes[Count].Path := Path;
      end;
    end;
  end;
end;

procedure EndInstallationProcesses(Directory: String; IsEverything: Boolean);
var
  Processes: TRunningProcesses;
  Index: Integer;
  Process: NativeInt;
begin
  if not FindInstallationProcesses(Directory, IsEverything, Processes) then
  begin
    Log('Could not enumerate processes for termination: ' + Directory);
    exit;
  end;
  for Index := 0 to GetArrayLength(Processes) - 1 do
  begin
    Process := WindowsOpenProcess($101001, False, Processes[Index].Id);
    if Process = 0 then
      Log(Format('Could not open process %d for termination: %d.', [Processes[Index].Id, DLLGetLastError]))
    else
    try
      { Validate the path again on the handle being terminated, in case the PID
        was reused after enumeration. Never terminate by executable name alone. }
      if IsSameFile(ReadProcessPath(Process), Processes[Index].Path) then
      begin
        if not WindowsTerminateProcess(Process, 2) then
          Log(Format('Could not terminate %s: %d.', [Processes[Index].Path, DLLGetLastError]))
        else if WindowsWaitForSingleObject(Process, 2000) <> 0 then
          Log('Process termination did not finish promptly: ' + Processes[Index].Path);
      end;
    finally
      WindowsCloseHandle(Process);
    end;
  end;
end;

procedure IgnoreWaitWindowClose(Sender: TObject; var CanClose: Boolean);
begin
  CanClose := False;
end;

procedure RequestCooperativeExit(Directory: String; IsDesktopUser: Boolean);
var
  Dialog: TSetupForm;
  OwnerForm: TSetupForm;
  LabelControl: TNewStaticText;
  Progress: TNewProgressBar;
  Processes: TRunningProcesses;
  ResultCode: Integer;
  Deadline: Int64;
  IsSilent: Boolean;
begin
  if not FileExists(AddBackslash(Directory) + '{#AppExeName}') then
    exit;
  if IsUninstaller then
  begin
    IsSilent := UninstallSilent;
    OwnerForm := UninstallProgressForm;
  end
  else
  begin
    IsSilent := WizardSilent;
    OwnerForm := WizardForm;
  end;
  Dialog := nil;
  try
    if not IsSilent then
    begin
      Dialog := CreateCustomForm(ScaleX(380), ScaleY(110), False, False);
      Dialog.Caption := '{#AppName}';
      Dialog.OnCloseQuery := @IgnoreWaitWindowClose;
      LabelControl := TNewStaticText.Create(Dialog);
      LabelControl.Parent := Dialog;
      LabelControl.SetBounds(ScaleX(16), ScaleY(16), ScaleX(348), ScaleY(24));
      LabelControl.Caption := ExpandConstant('{cm:WaitingForExit}');
      Progress := TNewProgressBar.Create(Dialog);
      Progress.Parent := Dialog;
      Progress.SetBounds(ScaleX(16), ScaleY(48), ScaleX(348), ScaleY(18));
      Progress.Style := npbstMarquee;
      LabelControl := TNewStaticText.Create(Dialog);
      LabelControl.Parent := Dialog;
      LabelControl.SetBounds(ScaleX(16), ScaleY(78), ScaleX(348), ScaleY(20));
      LabelControl.Caption := ExpandConstant('{cm:WaitingForExitDetail}');
      OwnerForm.Enabled := False;
      Dialog.Show;
      PumpMessages();
    end;
    Deadline := WindowsGetTickCount64() + ShutdownTimeout;
    RunController(AddBackslash(Directory) + '{#AppExeName}', '--hosts-control shutdown', IsDesktopUser, ShutdownTimeout, ResultCode);
    Log(Format('Cooperative shutdown returned code %d.', [ResultCode]));
    while WindowsGetTickCount64() < Deadline do
    begin
      if not FindInstallationProcesses(Directory, False, Processes) then
        break;
      if GetArrayLength(Processes) = 0 then
        break;
      Sleep(50);
      PumpMessages();
    end;
  finally
    if Dialog <> nil then
    begin
      Dialog.Free;
      OwnerForm.Enabled := True;
      OwnerForm.BringToFront;
    end;
  end;
end;

function EnsureInstallationClosed(Directory: String; CanRequestShutdown: Boolean; IsDesktopUser: Boolean): String;
var
  Processes: TRunningProcesses;
  Response: Integer;
  HasInspected: Boolean;
  Prompt: String;
begin
  Result := '';
  repeat
    HasInspected := FindInstallationProcesses(Directory, False, Processes);
    if HasInspected and (GetArrayLength(Processes) = 0) then
      exit;
    if HasInspected and CanRequestShutdown and (not HasCommandLineParameter('/NOCLOSEAPPLICATIONS')) then
    begin
      RequestCooperativeExit(Directory, IsDesktopUser);
      HasInspected := FindInstallationProcesses(Directory, False, Processes);
      if HasInspected and (GetArrayLength(Processes) = 0) then
        exit;
    end;
    if HasInspected then
      Prompt := ExpandConstant('{cm:PreviousInstallationInUse}')
    else
    begin
      Prompt := FmtMessage(ExpandConstant('{cm:PreviousInstallationCheckFailed}'), [IntToStr(DLLGetLastError)]);
      Log('Could not inspect running processes for: ' + Directory);
    end;
    if WizardSilent then
    begin
      Result := Prompt;
      exit;
    end;
    Response := SuppressibleTaskDialogMsgBox(ExpandConstant('{cm:ClosingEverywhere}'),
      Prompt + #13#10#13#10 + Directory,
      mbConfirmation, MB_YESNOCANCEL, [ExpandConstant('{cm:RetryExit}'),
      ExpandConstant('{cm:ContinueExit}'), ExpandConstant('{cm:CancelExit}')], 0, IDCANCEL);
    if Response = IDNO then
    begin
      { Continue means best-effort termination, not ignoring an uninstall error.
        Any remaining file locks are handled by subsequent file operations. }
      EndInstallationProcesses(Directory, False);
      exit;
    end;
    if Response <> IDYES then
      WindowsExitProcess(2);
  until False;
end;

function RunPreviousUninstaller(Uninstaller: String; IsDesktopUser: Boolean; IsUpgrade: Boolean): Boolean;
var
  FileName: String;
  Parameters: String;
  ResultCode: Integer;
  ShouldShowUninstallProgress: Boolean;
  WasCancelEnabled: Boolean;
  HasIgnoredWaitFailure: Boolean;
begin
  Result := False;
  ShouldShowUninstallProgress := not WizardSilent;
  repeat
    FileName := ExtractCommandExecutable(Uninstaller);
    ResultCode := 2; { ERROR_FILE_NOT_FOUND }
    if (FileName <> '') and FileExists(FileName) then
    begin
      if ShouldShowUninstallProgress then
        Parameters := '/SILENT'
      else
        Parameters := '/VERYSILENT';
      Parameters := Parameters + ' /SUPPRESSMSGBOXES /NORESTART /LOG /ALLOWINCOMPLETECLEANUP';
      if IsUpgrade then
        Parameters := Parameters + ' /UPGRADE';
      WizardForm.PreparingLabel.Caption := ExpandConstant('{cm:RemovingPreviousVersion}');
      { Native uninstall cannot be cancelled once removal begins. Transfer the
        visible UI to its progress window and restore Setup on every return. }
      WasCancelEnabled := WizardForm.CancelButton.Enabled;
      WizardForm.CancelButton.Enabled := False;
      try
        if ShouldShowUninstallProgress then
          WizardForm.Hide;
        Result := ExecutePreviousUninstaller(FileName, Parameters, IsDesktopUser, ResultCode, HasIgnoredWaitFailure);
      finally
        WizardForm.CancelButton.Enabled := WasCancelEnabled;
        if ShouldShowUninstallProgress then
        begin
          WizardForm.Show;
          WizardForm.BringToFront;
        end;
      end;
      if Result then
        exit;
      if HasIgnoredWaitFailure then
        exit;
    end;
    Log(Format('Previous uninstall did not complete: %s, code %d.', [FileName, ResultCode]));
    if not ShouldRetryPreviousUninstall(ResultCode, False) then
      exit;
  until False;
end;

procedure RegisterInstallResource(InstallDirectory: String; FileName: String);
begin
  if InstallDirectory <> '' then
    RegisterExtraCloseApplicationsResource(AddBackslash(InstallDirectory) + FileName);
end;

procedure RegisterExtraCloseApplicationsResources();
var
  Index: Integer;
begin
  for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
  begin
    RegisterInstallResource(PreviousInstallations[Index].Directory, '{#AppExeName}');
    RegisterInstallResource(PreviousInstallations[Index].Directory, 'Everywhere.Windows.exe');
    RegisterInstallResource(PreviousInstallations[Index].Directory, 'Everywhere.Watchdog.exe');
  end;
end;

function DeleteTaskOwnedBy(TaskName: String; ExecutablePath: String; var ErrorDetail: String): Boolean;
var
  Scheduler: Variant;
  RootFolder: Variant;
  Tasks: Variant;
  Task: Variant;
  Definition: Variant;
  Action: Variant;
  ConfiguredExecutablePath: String;
  Index: Integer;
  IsFound: Boolean;
begin
  Result := False;
  ErrorDetail := '';
  if ExecutablePath = '' then
  begin
    Result := True;
    exit;
  end;

  try
    Scheduler := CreateOleObject('Schedule.Service');
    Scheduler.Connect;
    RootFolder := Scheduler.GetFolder('\');
    Tasks := RootFolder.GetTasks(1);
    IsFound := False;
    for Index := 1 to Tasks.Count do
    begin
      Task := Tasks.Item(Index);
      if CompareText(Task.Name, TaskName) = 0 then
      begin
        IsFound := True;
        break;
      end;
    end;

    if not IsFound then
    begin
      Result := True;
      exit;
    end;

    Definition := Task.Definition;
    if Definition.Actions.Count <> 1 then
    begin
      Log(Format('Preserving task "%s" because it does not contain exactly one action.', [TaskName]));
      Result := True;
      exit;
    end;

    Action := Definition.Actions.Item(1);
    ConfiguredExecutablePath := Action.Path;
    if not IsSameFile(ConfiguredExecutablePath, ExecutablePath) then
    begin
      Log(Format('Preserving task "%s" owned by another executable: %s', [TaskName, ConfiguredExecutablePath]));
      Result := True;
      exit;
    end;

    RootFolder.DeleteTask(TaskName, 0);
    Log(Format('Removed task "%s" owned by %s.', [TaskName, ExecutablePath]));
    Result := True;
  except
    ErrorDetail := GetExceptionMessage;
    Log(Format('Failed to inspect or remove task "%s": %s', [TaskName, ErrorDetail]));
  end;
end;

procedure AppendErrorDetail(var ErrorDetail: String; Context: String; Detail: String);
begin
  if Detail = '' then
    Detail := 'unknown error';
  if ErrorDetail <> '' then
    ErrorDetail := ErrorDetail + '; ';
  ErrorDetail := ErrorDetail + Context + ': ' + Detail;
end;

function CleanupOwnedTasks(ExecutablePath: String; var ErrorDetail: String): Boolean;
var
  ItemError: String;
begin
  Result := True;
  ErrorDetail := '';
  if not DeleteTaskOwnedBy('Everywhere Hosts', ExecutablePath, ItemError) then
  begin
    AppendErrorDetail(ErrorDetail, 'Everywhere Hosts task', ItemError);
    Result := False;
  end;
  if not DeleteTaskOwnedBy('Everywhere', ExecutablePath, ItemError) then
  begin
    AppendErrorDetail(ErrorDetail, 'legacy Everywhere task', ItemError);
    Result := False;
  end;
end;

function DeleteEverywhereAutorunAtRoot(RootKey: HKEY; Subkey: String; ExecutablePath: String; var ErrorDetail: String): Boolean;
var
  Command: String;
begin
  Result := True;
  if not RegValueExists(RootKey, Subkey, '{#AppName}') then
    exit;

  if not RegQueryStringValue(RootKey, Subkey, '{#AppName}', Command) or
     (not IsSameFile(ExtractCommandExecutable(Command), ExecutablePath)) then
    exit;
  if not RegDeleteValue(RootKey, Subkey, '{#AppName}') then
  begin
    ErrorDetail := Subkey;
    Result := False;
    exit;
  end;

  Log(Format('Removed the Everywhere autorun value from %s.', [Subkey]));
end;

function CleanupEverywhereAutoruns(ExecutablePath: String; var ErrorDetail: String): Boolean;
var
  UserHives: TArrayOfString;
  Index: Integer;
  RunKey: String;
  ItemError: String;
begin
  Result := True;
  ErrorDetail := '';
  if not DeleteEverywhereAutorunAtRoot(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', ExecutablePath, ItemError) then
  begin
    AppendErrorDetail(ErrorDetail, 'current-user startup entry', ItemError);
    Result := False;
  end;

  if not RegGetSubkeyNames(HKEY_USERS, '', UserHives) then
  begin
    AppendErrorDetail(ErrorDetail, 'loaded-user startup entries', 'HKEY_USERS could not be enumerated');
    Result := False;
    exit;
  end;

  for Index := 0 to GetArrayLength(UserHives) - 1 do
  begin
    RunKey := UserHives[Index] + '\Software\Microsoft\Windows\CurrentVersion\Run';
    if not DeleteEverywhereAutorunAtRoot(HKEY_USERS, RunKey, ExecutablePath, ItemError) then
    begin
      AppendErrorDetail(ErrorDetail, 'startup entry ' + RunKey, ItemError);
      Result := False;
    end;
  end;
end;

function ShouldInstallServiceMode(): Boolean;
var
  InstallDirectory: String;
  ProgramFilesDirectory: String;
  Scheduler: Variant;
  Tasks: Variant;
  Index: Integer;
begin
  Result := False;
  { Only a first installation under the system Program Files directory opts in.
    Existing registrations represent the user's choice, including no task.
    Directory permissions and redirection are the administrator's responsibility. }
  if GetArrayLength(PreviousInstallations) > 0 then
    exit;
  InstallDirectory := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')));
  ProgramFilesDirectory := AddBackslash(ExpandFileName(ExpandConstant('{commonpf64}')));
  if CompareText(Copy(InstallDirectory, 1, Length(ProgramFilesDirectory)), ProgramFilesDirectory) <> 0 then
    exit;
  try
    Scheduler := CreateOleObject('Schedule.Service');
    Scheduler.Connect;
    Tasks := Scheduler.GetFolder('\').GetTasks(1);
    for Index := 1 to Tasks.Count do
      if CompareText(Tasks.Item(Index).Name, 'Everywhere Hosts') = 0 then
        exit;
    Result := True;
  except
    Log('Could not query service-mode registration; preserving existing configuration: ' + GetExceptionMessage);
  end;
end;

function ReadShortcutTarget(Path: String): String;
var
  Shell: Variant;
  Shortcut: Variant;
begin
  Result := '';
  if (Path = '') or (not FileExists(Path)) then
    exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(Path);
    Result := Shortcut.TargetPath;
  except
    Log('Could not inspect previous shortcut: ' + GetExceptionMessage);
  end;
end;

function IsApplicationTarget(Target: String; InstallDirectory: String): Boolean;
begin
  Result := (InstallDirectory <> '') and
    (IsSameFile(Target, AddBackslash(InstallDirectory) + '{#AppExeName}') or
     IsSameFile(Target, AddBackslash(InstallDirectory) + 'Everywhere.Windows.exe'));
end;

function RemovePreviousShortcut(Path: String; InstallDirectory: String; Uninstaller: String): Boolean;
var
  Target: String;
begin
  Result := True;
  Target := ReadShortcutTarget(Path);
  if IsApplicationTarget(Target, InstallDirectory) or
     IsSameFile(Target, ExtractCommandExecutable(Uninstaller)) or
     ((InstallDirectory <> '') and
      (IsSameFile(Target, AddBackslash(InstallDirectory) + 'Uninstall.exe') or
       IsSameFile(Target, AddBackslash(InstallDirectory) + 'unins000.exe'))) then
    if not DeleteFile(Path) then
    begin
      Log('Could not remove previous shortcut: ' + Path);
      Result := False;
    end;
end;

function CleanupPreviousShortcuts(InstallDirectory: String; Uninstaller: String): Boolean;
begin
  Result := True;
  if InstallDirectory = '' then
    exit;
  if not RemovePreviousShortcut(ExpandConstant('{commondesktop}\{#AppName}.lnk'), InstallDirectory, Uninstaller) then
    Result := False;
  if not RemovePreviousShortcut(ExpandConstant('{commonprograms}\{#AppName}.lnk'), InstallDirectory, Uninstaller) then
    Result := False;
  if not RemovePreviousShortcut(ExpandConstant('{commonprograms}\{#AppName}\Uninstall {#AppName}.lnk'), InstallDirectory, Uninstaller) then
    Result := False;
  if not RemovePreviousShortcut(ExpandConstant('{commonprograms}\{#AppName}\{#AppName}.lnk'), InstallDirectory, Uninstaller) then
    Result := False;
  if not RemovePreviousShortcut(ExpandConstant('{commonprograms}\{#AppName}\卸载 {#AppName}.lnk'), InstallDirectory, Uninstaller) then
    Result := False;
  { Remove only an empty shortcut group; unrelated contents are preserved. }
  RemoveDir(ExpandConstant('{commonprograms}\{#AppName}'));
  if DesktopUserDesktop <> '' then
    if not RemovePreviousShortcut(AddBackslash(DesktopUserDesktop) + '{#AppName}.lnk', InstallDirectory, Uninstaller) then
      Result := False;
  if DesktopUserPrograms <> '' then
  begin
    if not RemovePreviousShortcut(AddBackslash(DesktopUserPrograms) + '{#AppName}.lnk', InstallDirectory, Uninstaller) then
      Result := False;
    if not RemovePreviousShortcut(AddBackslash(DesktopUserPrograms) + '{#AppName}\Uninstall {#AppName}.lnk', InstallDirectory, Uninstaller) then
      Result := False;
    if not RemovePreviousShortcut(AddBackslash(DesktopUserPrograms) + '{#AppName}\{#AppName}.lnk', InstallDirectory, Uninstaller) then
      Result := False;
    if not RemovePreviousShortcut(AddBackslash(DesktopUserPrograms) + '{#AppName}\卸载 {#AppName}.lnk', InstallDirectory, Uninstaller) then
      Result := False;
    RemoveDir(AddBackslash(DesktopUserPrograms) + '{#AppName}');
  end;
end;

function IsPreviousApplicationTarget(Target: String; MustBeRemoved: Boolean): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
    if ((not MustBeRemoved) or PreviousInstallations[Index].HasRemoved) and
       IsApplicationTarget(Target, PreviousInstallations[Index].Directory) then
    begin
      Result := True;
      exit;
    end;
end;

procedure CapturePreviousDesktopShortcuts();
begin
  if HasCapturedPreviousDesktopShortcuts then
    exit;
  { Snapshot only the two installer-owned names, before old uninstall can remove
    them. A user's existing shortcut does not authorize a new public shortcut. }
  PreviousCommonDesktopTarget := ReadShortcutTarget(ExpandConstant('{commondesktop}\{#AppName}.lnk'));
  if not IsPreviousApplicationTarget(PreviousCommonDesktopTarget, False) then
    PreviousCommonDesktopTarget := '';
  if DesktopUserDesktop <> '' then
    PreviousUserDesktopTarget := ReadShortcutTarget(AddBackslash(DesktopUserDesktop) + '{#AppName}.lnk');
  if not IsPreviousApplicationTarget(PreviousUserDesktopTarget, False) then
    PreviousUserDesktopTarget := '';
  HasCapturedPreviousDesktopShortcuts := True;
end;

function RecreateInstalledShortcut(Path: String; Target: String; Parameters: String): Boolean;
begin
  Result := False;
  { Never edit an old link in place. Retirement already removed known old links;
    an occupied destination must still belong to this target before replacement. }
  if FileExists(Path) then
  begin
    if not IsSameFile(ReadShortcutTarget(Path), Target) then
    begin
      Log('Preserving a shortcut with a different or unreadable target: ' + Path);
      exit;
    end;
    if not DeleteFile(Path) then
    begin
      Log('Could not replace shortcut: ' + Path);
      exit;
    end;
  end;
  if not ForceDirectories(ExtractFileDir(Path)) then
  begin
    Log('Could not create shortcut directory: ' + ExtractFileDir(Path));
    exit;
  end;
  try
    CreateShellLink(Path, '', Target, Parameters, ExpandConstant('{app}'), '', 0, SW_SHOWNORMAL);
    Result := True;
  except
    Log('Could not create shortcut: ' + Path + ': ' + GetExceptionMessage);
  end;
end;

procedure UpdateInstalledShortcuts();
var
  Index: Integer;
  HasShortcutFailure: Boolean;
begin
  { Called only after payload installation succeeds. Scope changes require
    retirement even when the executable directory did not change. Ignored
    uninstall failures retain their shortcuts and repair registration. }
  for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
    if PreviousInstallations[Index].HasRemoved then
      if not CleanupPreviousShortcuts(PreviousInstallations[Index].Directory, PreviousInstallations[Index].Uninstaller) then
        Log('Some previous shortcuts could not be retired.');

  HasShortcutFailure := not RecreateInstalledShortcut(ExpandConstant('{commonprograms}\{#AppName}.lnk'),
    ExpandConstant('{app}\{#AppExeName}'), '');
  if not RecreateInstalledShortcut(ExpandConstant('{commonprograms}\{#AppName}\Uninstall {#AppName}.lnk'),
    ExpandConstant('{uninstallexe}'), '/LOG') then
    HasShortcutFailure := True;

  if IsPreviousApplicationTarget(PreviousCommonDesktopTarget, True) or
     ((PreviousCommonDesktopTarget = '') and (PreviousUserDesktopTarget = '') and WizardIsTaskSelected('desktopicon')) then
    if not RecreateInstalledShortcut(ExpandConstant('{commondesktop}\{#AppName}.lnk'),
      ExpandConstant('{app}\{#AppExeName}'), '') then
      HasShortcutFailure := True;
  if (DesktopUserDesktop <> '') and IsPreviousApplicationTarget(PreviousUserDesktopTarget, True) then
    if not RecreateInstalledShortcut(AddBackslash(DesktopUserDesktop) + '{#AppName}.lnk',
      ExpandConstant('{app}\{#AppExeName}'), '') then
      HasShortcutFailure := True;
  if HasShortcutFailure then
  begin
    Log('Some installation shortcuts could not be created.');
    if not WizardSilent then
      SuppressibleMsgBox(ExpandConstant('{cm:ShortcutCreationFailed}'), mbError, MB_OK, IDOK);
  end;
end;

function IsPathWithin(Path: String; Directory: String): Boolean;
begin
  Result := (Path <> '') and (Directory <> '') and
    (CompareText(Copy(AddBackslash(ExpandFileName(Path)), 1, Length(AddBackslash(ExpandFileName(Directory)))),
      AddBackslash(ExpandFileName(Directory))) = 0);
end;

function IsSafeLegacyDirectory(Directory: String): Boolean;
var
  Path: String;
  Parent: String;
  Index: Integer;
  Attributes: Cardinal;
begin
  Result := False;
  if (Length(Directory) < 4) or (Copy(Directory, 2, 2) <> ':\') then
    exit;
  Path := RemoveBackslashUnlessRoot(ExpandFileName(Directory));
  if (Length(Path) <= 3) or
     IsPathWithin(ExpandConstant('{win}'), Path) or
     IsPathWithin(ExpandConstant('{commonpf64}'), Path) or
     IsPathWithin(ExpandConstant('{commonpf32}'), Path) or
     IsPathWithin(ExpandConstant('{commonappdata}'), Path) or
     IsPathWithin(DesktopUserLocalAppData, Path) then
    exit;
  for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
    if not IsSameDirectory(Path, PreviousInstallations[Index].Directory) and
       IsPathWithin(PreviousInstallations[Index].Directory, Path) then
      exit;
  { Do not accept a registered directory whose ancestors redirect deletion. }
  repeat
    Attributes := WindowsGetFileAttributes(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then
      exit;
    Parent := ExtractFileDir(Path);
    if IsSameDirectory(Parent, Path) then
      break;
    Path := Parent;
  until Length(Path) <= 3;
  Result := True;
end;

procedure CaptureDeviceId();
var
  Size: Int64;
begin
  if HasCapturedDeviceId then
    exit;
  if DesktopUserLocalAppData = '' then
  begin
    Log('Could not locate Local AppData to read the device ID.');
    exit;
  end;
  DeviceIdPath := AddBackslash(DesktopUserLocalAppData) + '{#AppName}\.device_id';
  if FileExists(DeviceIdPath) then
  begin
    if not FileSize64(DeviceIdPath, Size) then
    begin
      Log('Could not read the device ID file size: ' + DeviceIdPath);
      exit;
    end;
    if Size = 36 then
    begin
      if not LoadStringFromFile(DeviceIdPath, DeviceIdContents) or (Length(DeviceIdContents) <> 36) then
      begin
        Log('Could not read the 36-byte device ID: ' + DeviceIdPath);
        exit;
      end;
      HasSavedDeviceId := True;
    end;
  end;
  HasCapturedDeviceId := True;
end;

function RestoreDeviceId(): Boolean;
begin
  Result := True;
  if not HasSavedDeviceId then
    exit;
  Result := ForceDirectories(ExtractFileDir(DeviceIdPath));
  if Result then
    Result := SaveStringToFile(DeviceIdPath, DeviceIdContents, False);
  NeedsDeviceIdRestore := not Result;
  if not Result then
    Log('Could not restore the device ID: ' + DeviceIdPath);
end;

function DeleteLegacyDirectory(Directory: String): Boolean;
var
  FindRec: TFindRec;
  Path: String;
begin
  Result := True;
  if not DirExists(Directory) then
    exit;
  if FindFirst(AddBackslash(Directory) + '*', FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Path := AddBackslash(Directory) + FindRec.Name;
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          { Remove a junction/symlink itself, never descend into its target. }
          if (FindRec.Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
          begin
            if not RemoveDir(Path) then
              Result := False;
          end
          else if not DeleteLegacyDirectory(Path) then
            Result := False;
        end
        else if not DeleteFile(Path) then
        begin
          Log('Could not delete legacy file: ' + Path);
          Result := False;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
  if not RemoveDir(Directory) then
  begin
    Log('Could not remove legacy directory: ' + Directory);
    Result := False;
  end;
end;

procedure CleanupLegacyIntegration(Directory: String);
var
  ExecutablePath: String;
  Value: String;
  Key: String;
  ErrorDetail: String;
  Index: Integer;
begin
  for Index := 0 to 1 do
  begin
    if Index = 0 then
      ExecutablePath := AddBackslash(Directory) + '{#AppExeName}'
    else
      ExecutablePath := AddBackslash(Directory) + 'Everywhere.Windows.exe';
    if not CleanupOwnedTasks(ExecutablePath, ErrorDetail) then
      Log('Could not fully clean legacy tasks: ' + ErrorDetail);
    if not DeleteEverywhereAutorunAtRoot(DesktopUserRoot,
      DesktopUserHive + 'Software\Microsoft\Windows\CurrentVersion\Run', ExecutablePath, ErrorDetail) then
      Log('Could not clean the legacy startup entry: ' + ErrorDetail);
  end;
  Key := DesktopUserHive + 'Software\Classes\sylinko-everywhere';
  if RegQueryStringValue(DesktopUserRoot, Key + '\shell\open\command', '', Value) and
     IsApplicationTarget(ExtractCommandExecutable(Value), Directory) then
    if not RegDeleteKeyIncludingSubkeys(DesktopUserRoot, Key) then
      Log('Could not remove the legacy URL association: ' + Key);
  Key := DesktopUserHive + 'Software\Classes\AppUserModelId\com.Sylinko.Everywhere';
  if RegQueryStringValue(DesktopUserRoot, Key, 'IconUri', Value) and
     IsSameFile(Value, AddBackslash(Directory) + 'Everywhere.ico') then
    if not RegDeleteKeyIncludingSubkeys(DesktopUserRoot, Key) then
      Log('Could not remove the legacy notification registration: ' + Key);
end;

procedure MigrateLegacyInstallation(Directory: String);
var
  Index: Integer;
begin
  CaptureDeviceId();
  { The destructive section does not pump messages or launch old uninstallers.
    Attempt to restore the original bytes before any retry, cancellation, or new
    payload. Cleanup and device ID failures never block the new installation. }
  NeedsDeviceIdRestore := HasSavedDeviceId;
  try
    CleanupLegacyIntegration(Directory);
    for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
      if IsSameDirectory(Directory, PreviousInstallations[Index].Directory) then
        if not CleanupPreviousShortcuts(Directory, PreviousInstallations[Index].Uninstaller) then
          Log('Could not fully clean legacy shortcuts for: ' + Directory);
    if IsSafeLegacyDirectory(Directory) then
    begin
      if not DeleteLegacyDirectory(Directory) then
        Log('Some legacy files remain in: ' + Directory);
    end
    else
      Log('Skipping recursive deletion of an unsafe legacy directory: ' + Directory);
  finally
    RestoreDeviceId();
  end;
end;

procedure RemovePreviousRegistration(Index: Integer);
var
  Directory: String;
begin
  Directory := PreviousInstallations[Index].Directory;
  if RegQueryStringValue(DesktopUserRoot, DesktopUserHive + 'Software\Everywhere', 'InstallDir', Directory) and
     IsSameDirectory(Directory, PreviousInstallations[Index].Directory) then
    if not RegDeleteKeyIncludingSubkeys(DesktopUserRoot, DesktopUserHive + 'Software\Everywhere') then
      Log('Could not remove the legacy installation location record: ' + Directory);
  if RegKeyExists(PreviousInstallations[Index].RootKey, PreviousInstallations[Index].Key) then
    if not RegDeleteKeyIncludingSubkeys(PreviousInstallations[Index].RootKey, PreviousInstallations[Index].Key) then
      Log('Could not remove the previous uninstall registration: ' + PreviousInstallations[Index].Key);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Index: Integer;
  OtherIndex: Integer;
  OwnerIndex: Integer;
  Directory: String;
  CanRequestShutdown: Boolean;
  HasRemoved: Boolean;
  HasProcessedDirectory: Boolean;
begin
  Result := '';
  if HasPreparedInstallation then
    exit;
  IsPreparingInstallation := True;
  try
    CapturePreviousDesktopShortcuts();
    for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
    begin
      if PreviousInstallations[Index].HasRemoved then
        continue;
      Directory := PreviousInstallations[Index].Directory;
      HasProcessedDirectory := False;
      for OtherIndex := 0 to Index - 1 do
        if IsSameDirectory(Directory, PreviousInstallations[OtherIndex].Directory) then
          HasProcessedDirectory := True;
      if HasProcessedDirectory then
        continue;
      OwnerIndex := Index;
      CanRequestShutdown := PreviousInstallations[Index].ShutdownProtocolVersion = 1;
      { An old NSIS registration beside a modern Inno registration must not
        downgrade the shared directory into recursive legacy cleanup. }
      for OtherIndex := 0 to GetArrayLength(PreviousInstallations) - 1 do
        if IsSameDirectory(Directory, PreviousInstallations[OtherIndex].Directory) then
        begin
          if PreviousInstallations[OtherIndex].LayoutVersion > PreviousInstallations[OwnerIndex].LayoutVersion then
            OwnerIndex := OtherIndex;
          CanRequestShutdown := CanRequestShutdown or (PreviousInstallations[OtherIndex].ShutdownProtocolVersion = 1);
        end;
      Result := EnsureInstallationClosed(Directory, CanRequestShutdown,
        (DesktopUserToken <> 0) or PreviousInstallations[OwnerIndex].IsDesktopUser);
      if Result <> '' then
        exit;
      if PreviousInstallations[OwnerIndex].LayoutVersion < 2 then
      begin
        EndInstallationProcesses(Directory, True);
        WizardForm.CancelButton.Enabled := False;
        try
          MigrateLegacyInstallation(Directory);
        finally
          WizardForm.CancelButton.Enabled := True;
        end;
        HasRemoved := True;
      end
      else
        HasRemoved := RunPreviousUninstaller(PreviousInstallations[OwnerIndex].Uninstaller,
          PreviousInstallations[OwnerIndex].IsDesktopUser, IsSameDirectory(Directory, WizardDirValue));
      for OtherIndex := Index to GetArrayLength(PreviousInstallations) - 1 do
        if (OtherIndex = Index) or IsSameDirectory(Directory, PreviousInstallations[OtherIndex].Directory) then
          PreviousInstallations[OtherIndex].HasRemoved := HasRemoved;
    end;
    { Retire old registrations after best-effort migration. Residual files or
      registration cleanup failures must not block the new installation. }
    for Index := 0 to GetArrayLength(PreviousInstallations) - 1 do
      if PreviousInstallations[Index].HasRemoved then
        RemovePreviousRegistration(Index);
    HasPreparedInstallation := True;
  finally
    IsPreparingInstallation := False;
  end;
end;


procedure CurStepChanged(Step: TSetupStep);
var
  ResultCode: Integer;
  ProcessInfo: TNativeProcessInfo;
begin
  if Step = ssPostInstall then
  begin
    UpdateInstalledShortcuts();
    { Inno recreates its uninstall key when saving uninstall information. }
    if not (RegWriteDWordValue(HKEY_LOCAL_MACHINE, InstallRegistryKey, 'ShutdownProtocolVersion', 1) and
            RegWriteDWordValue(HKEY_LOCAL_MACHINE, InstallRegistryKey, 'InstallLayoutVersion', 2)) then
    begin
      Log('Could not register installation layout.');
      exit;
    end;
    if not ShouldInstallServiceMode() then
      exit;
    if not RunController(ExpandConstant('{app}\{#AppExeName}'), '--hosts-control install', False, 10000, ResultCode) then
      Log(Format('Service installation failed with code %d.', [ResultCode]));
  end
  else if (Step = ssDone) and (not WizardSilent) and LaunchCheckBox.Visible and LaunchCheckBox.Checked then
  begin
    { Use the desktop shell's identity, even if Setup was launched elevated.
      Never fall back to launching the interactive app with Setup's identity. }
    if StartProgram(ExpandConstant('{app}\{#AppExeName}'), '', True, 0, ProcessInfo, ResultCode) then
    begin
      WindowsCloseHandle(ProcessInfo.Thread);
      WindowsCloseHandle(ProcessInfo.Process);
    end;
  end;
end;

procedure DeinitializeSetup();
begin
  if NeedsDeviceIdRestore then
    RestoreDeviceId();
  if DesktopUserToken <> 0 then
    WindowsCloseHandle(DesktopUserToken);
end;

function InitializeUninstall(): Boolean;
begin
  CaptureDesktopUser();
  Result := True;
end;

procedure DeinitializeUninstall();
begin
  if DesktopUserToken <> 0 then
    WindowsCloseHandle(DesktopUserToken);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  ErrorDetail: String;
begin
  if Step <> usUninstall then
    exit;
  { Uninstall shares best-effort resource cleanup with Setup. File-in-use errors
    remain the native uninstaller's responsibility, not a second dialog flow. }
  if FileExists(ExpandConstant('{app}\{#AppExeName}')) then
  begin
    UninstallProgressForm.StatusLabel.Caption := ExpandConstant('{cm:ClosingEverywhere}');
    RequestCooperativeExit(ExpandConstant('{app}'), DesktopUserToken <> 0);
  end;
  { Only same-directory replacement requests preservation. Older uninstallers
    ignore this option; their historical state reset is intentionally accepted. }
  if HasCommandLineParameter('/UPGRADE') then
    exit;
  CleanupPreviousShortcuts(ExpandConstant('{app}'), ExpandConstant('{uninstallexe}'));
  if not CleanupOwnedTasks(ExpandConstant('{app}\{#AppExeName}'), ErrorDetail) then
    Log('Uninstall task cleanup failed: ' + ErrorDetail);
  if not CleanupEverywhereAutoruns(ExpandConstant('{app}\{#AppExeName}'), ErrorDetail) then
    Log('Uninstall startup cleanup failed: ' + ErrorDetail);
end;
