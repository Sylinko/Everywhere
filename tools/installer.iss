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
en.PreviousUninstallFailed=The previous version could not be fully removed (code %1). Retry, ignore this problem and continue installing, or cancel Setup. Ignoring may leave old files. See the Setup log for details.
zh.PreviousUninstallFailed=旧版本未能完整卸载（代码 %1）。你可以重试、忽略此问题并继续安装，或取消安装。忽略可能留下旧文件，详情请查看安装日志。
en.InstallWillCloseEverywhere=Everywhere will close when installation starts.
zh.InstallWillCloseEverywhere=安装开始时将关闭 Everywhere。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
Name: "{app}"; Flags: uninsalwaysuninstall

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Final uninstall removes owned shortcuts explicitly; upgrade uninstall preserves them.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Flags: uninsneveruninstall
Name: "{autoprograms}\{#AppName}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Parameters: "/LOG"; Flags: uninsneveruninstall
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; Flags: uninsneveruninstall

[Code]
const
  InstallRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D66EA41B-8DEB-4E5A-9D32-AB4F8305F664}}_is1';
  LegacyRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Everywhere';
  WaitTimeout = 258;
  CreateSuspended = $4;
  CreateUnicodeEnvironment = $400;
  CreateNoWindow = $08000000;

type
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
  HasPreviousMachineInstall: Boolean;
  PreviousMachineLayoutVersion: Cardinal;
  PreviousMachineShutdownProtocolVersion: Cardinal;
  PreviousMachineInstallDirectory: String;
  PreviousMachineUninstaller: String;
  HasPreviousUserInstall: Boolean;
  PreviousUserLayoutVersion: Cardinal;
  PreviousUserShutdownProtocolVersion: Cardinal;
  PreviousUserInstallDirectory: String;
  PreviousUserUninstaller: String;
  DesktopUserToken: NativeInt;
  DesktopUserHive: String;
  DesktopUserDesktop: String;
  DesktopUserPrograms: String;
  ActiveControllerJob: NativeInt;
  IsPreparingInstallation: Boolean;
  HasPreparedInstallation: Boolean;
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
    if not WindowsOpenProcessToken(Process, $E, Token) then
      exit;
    try
      if not WindowsDuplicateTokenEx(Token, $B, 0, 2, 1, DesktopUserToken) then
        exit;
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

function InitializeSetup(): Boolean;
begin
  CaptureDesktopUser();
  HasPreviousMachineInstall := QueryPreviousInstall(HKEY_LOCAL_MACHINE, InstallRegistryKey, PreviousMachineInstallDirectory, PreviousMachineUninstaller);
  RegQueryDWordValue(HKEY_LOCAL_MACHINE, InstallRegistryKey, 'InstallLayoutVersion', PreviousMachineLayoutVersion);
  RegQueryDWordValue(HKEY_LOCAL_MACHINE, InstallRegistryKey, 'ShutdownProtocolVersion', PreviousMachineShutdownProtocolVersion);
  if DesktopUserHive <> '' then
  begin
    HasPreviousUserInstall := QueryPreviousInstall(HKEY_USERS, DesktopUserHive + InstallRegistryKey, PreviousUserInstallDirectory, PreviousUserUninstaller);
    RegQueryDWordValue(HKEY_USERS, DesktopUserHive + InstallRegistryKey, 'InstallLayoutVersion', PreviousUserLayoutVersion);
    RegQueryDWordValue(HKEY_USERS, DesktopUserHive + InstallRegistryKey, 'ShutdownProtocolVersion', PreviousUserShutdownProtocolVersion);
  end
  else
  begin
    HasPreviousUserInstall := QueryPreviousInstall(HKEY_CURRENT_USER, InstallRegistryKey, PreviousUserInstallDirectory, PreviousUserUninstaller);
    RegQueryDWordValue(HKEY_CURRENT_USER, InstallRegistryKey, 'InstallLayoutVersion', PreviousUserLayoutVersion);
    RegQueryDWordValue(HKEY_CURRENT_USER, InstallRegistryKey, 'ShutdownProtocolVersion', PreviousUserShutdownProtocolVersion);
    Log('The desktop user could not be resolved; using the current account for legacy installation discovery.');
  end;
  Result := True;
end;

function GetDefaultInstallPath(Param: String): String;
begin
  if HasPreviousMachineInstall and (PreviousMachineLayoutVersion = 2) and (PreviousMachineInstallDirectory <> '') then
    Result := RemoveBackslashUnlessRoot(PreviousMachineInstallDirectory)
  else if HasPreviousUserInstall and (PreviousUserLayoutVersion = 2) and (PreviousUserInstallDirectory <> '') then
    Result := RemoveBackslashUnlessRoot(PreviousUserInstallDirectory)
  else
    Result := ExpandConstant('{autopf}\{#AppName}');
end;

function ShouldSkipPage(PageId: Integer): Boolean;
begin
  Result := (PageId = wpSelectDir) and (HasPreviousMachineInstall or HasPreviousUserInstall) and
    ((not HasPreviousMachineInstall) or (PreviousMachineLayoutVersion = 2)) and
    ((not HasPreviousUserInstall) or (PreviousUserLayoutVersion = 2));
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
  if HasPreviousMachineInstall or HasPreviousUserInstall then
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
begin
  Result := True;
  if PageId <> wpSelectDir then
    exit;
  if IsSameDirectory(WizardDirValue, PreviousMachineInstallDirectory) or
     IsSameDirectory(WizardDirValue, PreviousUserInstallDirectory) or
     (not IsDirectoryNotEmpty(WizardDirValue)) then
    exit;
  Result := SuppressibleMsgBox(ExpandConstant('{cm:InstallDirectoryNotEmpty}'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;

function StartProgram(FileName: String; Parameters: String; IsDesktopUser: Boolean; Flags: Cardinal; var ProcessInfo: TNativeProcessInfo): Boolean;
var
  Startup: TNativeStartupInfo;
  Environment: NativeInt;
  ErrorCode: Cardinal;
begin
  Result := False;
  Startup.Size := SizeOf(Startup);
  Startup.Flags := 1;
  Startup.ShowWindow := SW_HIDE;
  if (Flags and CreateNoWindow) = 0 then
    Startup.ShowWindow := SW_SHOWNORMAL;
  if IsDesktopUser then
  begin
    if DesktopUserToken = 0 then
      exit;
    if not WindowsCreateEnvironmentBlock(Environment, DesktopUserToken, False) then
      exit;
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
  if not Result then
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
    if not StartProgram(FileName, Parameters, IsDesktopUser, CreateSuspended or CreateNoWindow, ProcessInfo) then
      exit;
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

function ExecutePreviousUninstaller(FileName: String; Parameters: String; IsDesktopUser: Boolean; var ResultCode: Integer): Boolean;
var
  ProcessInfo: TNativeProcessInfo;
  WaitResult: Cardinal;
  ExitCode: Cardinal;
begin
  { Inno's original process waits until the actual uninstall work is complete.
    Follow that native lifetime; no forced cancellation or process-tree job. }
  Result := False;
  ResultCode := -1;
  if not IsDesktopUser then
  begin
    Result := Exec(FileName, Parameters, ExtractFileDir(FileName), SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
    Result := Result and (ResultCode = 0);
    exit;
  end;
  { Preserve the captured desktop identity for a user-scoped old installation,
    including when Setup was elevated using another account's credentials. }
  if not StartProgram(FileName, Parameters, True, 0, ProcessInfo) then
    exit;
  try
    repeat
      WaitResult := WindowsWaitForSingleObject(ProcessInfo.Process, 50);
      PumpMessages();
    until WaitResult <> WaitTimeout;
    if WaitResult <> 0 then
    begin
      Log('Could not wait for the previous uninstaller; stopping Setup.');
      WindowsExitProcess(7);
    end;
    if not WindowsGetExitCodeProcess(ProcessInfo.Process, ExitCode) then
    begin
      Log('Could not read the previous uninstall result; stopping Setup.');
      WindowsExitProcess(7);
    end;
    ResultCode := ExitCode;
    Result := ResultCode = 0;
  finally
    WindowsCloseHandle(ProcessInfo.Thread);
    WindowsCloseHandle(ProcessInfo.Process);
  end;
end;

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
  WindowsExitProcess(2);
end;

procedure CloseInstallation(InstallDirectory: String; CanRequestShutdown: Boolean; IsDesktopUser: Boolean);
var
  ResultCode: Integer;
  FileName: String;
begin
  FileName := AddBackslash(InstallDirectory) + '{#AppExeName}';
  if (InstallDirectory = '') or (not CanRequestShutdown) or (not FileExists(FileName)) then
    exit;
  if HasCommandLineParameter('/NOCLOSEAPPLICATIONS') then
    exit;
  WizardForm.PreparingLabel.Caption := ExpandConstant('{cm:ClosingEverywhere}');
  RunController(FileName, '--hosts-control shutdown', IsDesktopUser, 35000, ResultCode);
  Log(Format('Cooperative shutdown returned code %d.', [ResultCode]));
end;

function RunPreviousUninstaller(Uninstaller: String; IsDesktopUser: Boolean; IsUpgrade: Boolean): Boolean;
var
  FileName: String;
  Parameters: String;
  ResultCode: Integer;
  Response: Integer;
  ShouldShowUninstallProgress: Boolean;
  WasCancelEnabled: Boolean;
begin
  Result := False;
  ShouldShowUninstallProgress := not WizardSilent;
  repeat
    FileName := ExtractCommandExecutable(Uninstaller);
    ResultCode := -1;
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
        Result := ExecutePreviousUninstaller(FileName, Parameters, IsDesktopUser, ResultCode);
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
    end;
    Log(Format('Previous uninstall did not complete: %s, code %d.', [FileName, ResultCode]));
    { Unattended callers must opt in explicitly; suppressed dialogs never hang. }
    if HasCommandLineParameter('/IGNOREUNINSTALLFAILURE') then
      exit;
    if WizardSilent or HasCommandLineParameter('/SUPPRESSMSGBOXES') then
      WindowsExitProcess(7);
    Response := MsgBox(FmtMessage(ExpandConstant('{cm:PreviousUninstallFailed}'), [IntToStr(ResultCode)]),
      mbError, MB_ABORTRETRYIGNORE or MB_DEFBUTTON1);
    if Response = IDIGNORE then
      exit;
    if Response = IDABORT then
      WindowsExitProcess(2);
  until False;
end;

procedure RegisterInstallResource(InstallDirectory: String; FileName: String);
begin
  if InstallDirectory <> '' then
    RegisterExtraCloseApplicationsResource(AddBackslash(InstallDirectory) + FileName);
end;

procedure RegisterExtraCloseApplicationsResources();
begin
  RegisterInstallResource(PreviousMachineInstallDirectory, '{#AppExeName}');
  RegisterInstallResource(PreviousMachineInstallDirectory, 'Everywhere.Watchdog.exe');
  RegisterInstallResource(PreviousUserInstallDirectory, '{#AppExeName}');
  RegisterInstallResource(PreviousUserInstallDirectory, 'Everywhere.Watchdog.exe');
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

function DeleteEverywhereAutorunAtRoot(RootKey: HKEY; Subkey: String; var ErrorDetail: String): Boolean;
var
  Command: String;
begin
  Result := True;
  if not RegValueExists(RootKey, Subkey, '{#AppName}') then
    exit;

  if not RegQueryStringValue(RootKey, Subkey, '{#AppName}', Command) or
     (not IsSameFile(ExtractCommandExecutable(Command), ExpandConstant('{app}\{#AppExeName}'))) then
    exit;
  if not RegDeleteValue(RootKey, Subkey, '{#AppName}') then
  begin
    ErrorDetail := Subkey;
    Result := False;
    exit;
  end;

  Log(Format('Removed the Everywhere autorun value from %s.', [Subkey]));
end;

function CleanupEverywhereAutoruns(var ErrorDetail: String): Boolean;
var
  UserHives: TArrayOfString;
  Index: Integer;
  RunKey: String;
  ItemError: String;
begin
  Result := True;
  ErrorDetail := '';
  if not DeleteEverywhereAutorunAtRoot(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', ItemError) then
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
    if not DeleteEverywhereAutorunAtRoot(HKEY_USERS, RunKey, ItemError) then
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
  if HasPreviousMachineInstall or HasPreviousUserInstall then
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

procedure RemovePreviousShortcut(Path: String; InstallDirectory: String; Uninstaller: String);
var
  Shell: Variant;
  Shortcut: Variant;
  Target: String;
begin
  if (Path = '') or (not FileExists(Path)) then
    exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(Path);
    Target := Shortcut.TargetPath;
    if IsSameFile(Target, AddBackslash(InstallDirectory) + '{#AppExeName}') or
       IsSameFile(Target, ExtractCommandExecutable(Uninstaller)) then
      if not DeleteFile(Path) then
        Log('Could not remove previous shortcut: ' + Path);
  except
    Log('Could not inspect previous shortcut: ' + GetExceptionMessage);
  end;
end;

procedure CleanupPreviousShortcuts(InstallDirectory: String; Uninstaller: String);
begin
  if InstallDirectory = '' then
    exit;
  RemovePreviousShortcut(ExpandConstant('{commondesktop}\{#AppName}.lnk'), InstallDirectory, Uninstaller);
  RemovePreviousShortcut(ExpandConstant('{commonprograms}\{#AppName}.lnk'), InstallDirectory, Uninstaller);
  RemovePreviousShortcut(ExpandConstant('{commonprograms}\{#AppName}\Uninstall {#AppName}.lnk'), InstallDirectory, Uninstaller);
  { Remove only an empty shortcut group; unrelated contents are preserved. }
  RemoveDir(ExpandConstant('{commonprograms}\{#AppName}'));
  if DesktopUserDesktop <> '' then
    RemovePreviousShortcut(AddBackslash(DesktopUserDesktop) + '{#AppName}.lnk', InstallDirectory, Uninstaller);
  if DesktopUserPrograms <> '' then
  begin
    RemovePreviousShortcut(AddBackslash(DesktopUserPrograms) + '{#AppName}.lnk', InstallDirectory, Uninstaller);
    RemovePreviousShortcut(AddBackslash(DesktopUserPrograms) + '{#AppName}\Uninstall {#AppName}.lnk', InstallDirectory, Uninstaller);
    RemoveDir(AddBackslash(DesktopUserPrograms) + '{#AppName}');
  end;
end;

procedure CleanupLegacyRegistryAtRoot(RootKey: HKEY; Prefix: String);
var
  Identifier: String;
begin
  if RegQueryStringValue(RootKey, Prefix + LegacyRegistryKey, 'Identifier', Identifier) and
     (Identifier = 'D66EA41B-8DEB-4E5A-9D32-AB4F8305F664') then
    if not RegDeleteKeyIncludingSubkeys(RootKey, Prefix + LegacyRegistryKey) then
      Log('Could not remove the legacy uninstall registration.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  HasRemovedMachine: Boolean;
  HasRemovedUser: Boolean;
  HasSharedPayload: Boolean;
begin
  Result := '';
  if HasPreparedInstallation then
    exit;
  HasSharedPayload := HasPreviousMachineInstall and HasPreviousUserInstall and
    (IsSameDirectory(PreviousUserInstallDirectory, PreviousMachineInstallDirectory) or
     IsSameFile(ExtractCommandExecutable(PreviousUserUninstaller), ExtractCommandExecutable(PreviousMachineUninstaller)));
  IsPreparingInstallation := True;
  WizardForm.CancelButton.Enabled := True;
  try
    CloseInstallation(PreviousMachineInstallDirectory, PreviousMachineShutdownProtocolVersion = 1, DesktopUserToken <> 0);
    if not IsSameDirectory(PreviousUserInstallDirectory, PreviousMachineInstallDirectory) then
      CloseInstallation(PreviousUserInstallDirectory, PreviousUserShutdownProtocolVersion = 1, True);
    if HasPreviousMachineInstall then
      HasRemovedMachine := RunPreviousUninstaller(PreviousMachineUninstaller, False,
        IsSameDirectory(PreviousMachineInstallDirectory, WizardDirValue));
    if HasPreviousUserInstall then
    begin
      if HasSharedPayload then
        HasRemovedUser := HasRemovedMachine
      else
        HasRemovedUser := RunPreviousUninstaller(PreviousUserUninstaller, True,
          IsSameDirectory(PreviousUserInstallDirectory, WizardDirValue));
    end;
    { Ignoring failure is not proof of removal. Preserve the old repair entry and
      shortcuts when its uninstaller did not complete. No preemptive cleanup. }
    if HasRemovedMachine and (not IsSameDirectory(PreviousMachineInstallDirectory, WizardDirValue)) then
      CleanupPreviousShortcuts(PreviousMachineInstallDirectory, PreviousMachineUninstaller);
    if HasRemovedUser then
    begin
      if not IsSameDirectory(PreviousUserInstallDirectory, WizardDirValue) then
        CleanupPreviousShortcuts(PreviousUserInstallDirectory, PreviousUserUninstaller);
      if DesktopUserHive <> '' then
      begin
        RegDeleteKeyIncludingSubkeys(HKEY_USERS, DesktopUserHive + InstallRegistryKey);
        CleanupLegacyRegistryAtRoot(HKEY_USERS, DesktopUserHive);
      end
      else
      begin
        RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, InstallRegistryKey);
        CleanupLegacyRegistryAtRoot(HKEY_CURRENT_USER, '');
      end;
    end;
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
    if StartProgram(ExpandConstant('{app}\{#AppExeName}'), '', True, 0, ProcessInfo) then
    begin
      WindowsCloseHandle(ProcessInfo.Thread);
      WindowsCloseHandle(ProcessInfo.Process);
    end;
  end;
end;

procedure DeinitializeSetup();
begin
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
  ResultCode: Integer;
  ErrorDetail: String;
begin
  if Step <> usUninstall then
    exit;
  { Uninstall shares best-effort resource cleanup with Setup. File-in-use errors
    remain the native uninstaller's responsibility, not a second dialog flow. }
  if FileExists(ExpandConstant('{app}\{#AppExeName}')) then
  begin
    UninstallProgressForm.StatusLabel.Caption := ExpandConstant('{cm:ClosingEverywhere}');
    RunController(ExpandConstant('{app}\{#AppExeName}'), '--hosts-control shutdown', DesktopUserToken <> 0, 35000, ResultCode);
    Log(Format('Uninstall shutdown returned code %d.', [ResultCode]));
  end;
  { Only same-directory replacement requests preservation. Older uninstallers
    ignore this option; their historical state reset is intentionally accepted. }
  if HasCommandLineParameter('/UPGRADE') then
    exit;
  CleanupPreviousShortcuts(ExpandConstant('{app}'), ExpandConstant('{uninstallexe}'));
  if not CleanupOwnedTasks(ExpandConstant('{app}\{#AppExeName}'), ErrorDetail) then
    Log('Uninstall task cleanup failed: ' + ErrorDetail);
  if not CleanupEverywhereAutoruns(ErrorDetail) then
    Log('Uninstall startup cleanup failed: ' + ErrorDetail);
end;
