#define AppName "Everywhere"
#define AppPublisher "Sylinko"
#define AppExeName "Everywhere.exe"
#define AppVersion GetEnv("VERSION")

[Setup]
; --- Basic Application Information ---
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}

; --- Installer Settings ---
DefaultDirName={code:GetDefaultInstallPath}
DefaultGroupName={#AppName}
OutputDir=..
OutputBaseFilename=Everywhere-Windows-x64-Setup-v{#AppVersion}
PrivilegesRequired=admin
UsePreviousAppDir=no
AllowUNCPath=no
AllowNetworkDrive=no
AppMutex=com.sylinko.everywhere
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

; --- UI and Icons ---
WizardStyle=modern dynamic
SetupIconFile=..\img\Everywhere.ico
UninstallDisplayIcon={app}\{#AppExeName}

; --- Registry ---
UninstallDisplayName={#AppName}
AppId={{D66EA41B-8DEB-4E5A-9D32-AB4F8305F664}}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
en.HostsTaskInstallFailed=Everywhere was installed and can be used normally, but it cannot currently interact with applications running as administrator. You can try enabling "Install as a service" later in Settings > General > System interaction.
zh.HostsTaskInstallFailed=Everywhere 已安装并可正常使用，但目前无法识别或操作以管理员身份运行的应用。你可以稍后前往“设置 → 通用 → 系统交互”，重新开启“以服务的方式安装”。
en.InstallIdentityRegistrationFailed=Everywhere was installed, but Windows could not finish registering this installation. Service mode has not been enabled. Run Setup again to repair it.
zh.InstallIdentityRegistrationFailed=Everywhere 已安装，但 Windows 未能完成安装信息注册，因此尚未启用服务模式。请重新运行安装程序进行修复。
en.UnprotectedInstallDirectory=This folder is outside Program Files, so other software may be able to modify Everywhere more easily. We recommend installing under a Program Files folder on any fixed drive.%n%nDo you want to continue with this folder?
zh.UnprotectedInstallDirectory=此文件夹不在 Program Files 下，其他软件可能更容易修改 Everywhere。建议安装到任意固定磁盘的 Program Files 文件夹中。%n%n仍要使用此文件夹吗？
en.InstallDirectoryNotEmpty=This folder already contains files. To avoid overwriting your data, choose an empty folder.
zh.InstallDirectoryNotEmpty=此文件夹中已有文件。为避免覆盖你的数据，请选择一个空文件夹。
en.InstallDirectoryUnavailable=Windows could not read from and write to this folder. Check the folder permissions or choose another folder, then try again. Details have been written to the Setup log.
zh.InstallDirectoryUnavailable=Windows 无法读取或写入此文件夹。请检查文件夹权限，或选择其他文件夹后重试。详细信息已写入安装日志。
en.PreviousUninstallFailed=The previous version could not be removed. Close Everywhere and try again. The uninstaller returned code %1.
zh.PreviousUninstallFailed=无法移除旧版本。请关闭 Everywhere 后重试。卸载程序返回了代码 %1。
en.PreviousUninstallerMissing=The uninstaller for the previous version is missing. Reinstall that version to repair its uninstaller, then try again.
zh.PreviousUninstallerMissing=找不到旧版本的卸载程序。请先重新安装旧版本以修复卸载程序，然后重试。
en.InstallDirectoryStillNotEmpty=The previous version was removed, but some files remain in this folder. Check the remaining files or choose another empty folder, then try again.
zh.InstallDirectoryStillNotEmpty=旧版本已移除，但此文件夹中仍有文件。请检查剩余文件，或选择其他空文件夹后重试。
en.InstallDirectoryProtectionFailed=Windows could not protect the installation folder before copying Everywhere. Continuing may make service mode more vulnerable to tampering.%n%nDo you want to continue anyway? Details have been written to the Setup log.
zh.InstallDirectoryProtectionFailed=Windows 无法在复制 Everywhere 前保护安装文件夹。继续安装可能使服务模式更容易受到恶意软件干扰。%n%n仍要继续吗？详细信息已写入安装日志。
en.PreviousInstallCleanupFailed=Some background services from the previous installation could not be removed.%n%nYou can abort installation and try again later, retry now, or ignore this problem and continue. If you continue, Windows may retain background startup entries that need to be removed manually. Details have been written to the Setup log.
zh.PreviousInstallCleanupFailed=旧版本的部分后台服务未能移除。%n%n你可以中止安装并稍后重试，也可以立即重试，或忽略此问题并继续。如果继续，Windows 中可能会留下需要手动清理的后台启动项。详细信息已写入安装日志。
en.UninstallCleanupFailed=Some background services could not be removed.%n%nYou can abort and try again later, retry now, or ignore this problem and continue uninstalling. If you continue, Windows may retain background startup entries that need to be removed manually. Details have been written to the uninstall log.
zh.UninstallCleanupFailed=部分后台服务未能移除。%n%n你可以中止并稍后重试，也可以立即重试，或忽略此问题并继续卸载。如果继续，Windows 中可能会留下需要手动清理的后台启动项。详细信息已写入卸载日志。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
Name: "{app}"; Flags: uninsalwaysuninstall

[Files]
; Copy all files from the publish directory to the installation directory {app}
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autoprograms}\{#AppName}\Uninstall {#AppName}"; Filename: "{uninstallexe}"; Parameters: "/LOG"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Code]
const
  DRIVE_FIXED = 3;
  ERROR_FILE_NOT_FOUND = 2;
  ERROR_PATH_NOT_FOUND = 3;
  ERROR_NO_MORE_FILES = 18;
  SE_FILE_OBJECT = 1;
  OWNER_SECURITY_INFORMATION = $1;
  DACL_SECURITY_INFORMATION = $4;
  PROTECTED_DACL_SECURITY_INFORMATION = $80000000;
  SDDL_REVISION_1 = 1;
  EverywhereFileAttributeDirectory = $10;
  EverywhereFileAttributeReparsePoint = $400;
  EverywhereInvalidFileAttributes = $FFFFFFFF;
  InstallRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D66EA41B-8DEB-4E5A-9D32-AB4F8305F664}}_is1';

type
  TDirectoryInspection = (diMissing, diEmpty, diNotEmpty, diUnavailable);

var
  HasPreviousMachineInstall: Boolean;
  PreviousMachineLayoutVersion: Cardinal;
  PreviousMachineInstallDirectory: String;
  PreviousMachineUninstaller: String;
  HasPreviousUserInstall: Boolean;
  PreviousUserInstallDirectory: String;
  PreviousUserUninstaller: String;
  ApprovedUnprotectedDirectory: String;
  ShouldAllowIncompletePreviousCleanup: Boolean;

function WindowsGetDriveType(RootPathName: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function WindowsGetFileAttributes(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function WindowsConvertStringSecurityDescriptorToSecurityDescriptor(
  StringSecurityDescriptor: String;
  StringSDRevision: DWORD;
  var SecurityDescriptor: NativeInt;
  SecurityDescriptorSize: NativeInt): BOOL;
  external 'ConvertStringSecurityDescriptorToSecurityDescriptorW@advapi32.dll stdcall';

function WindowsGetSecurityDescriptorDacl(
  SecurityDescriptor: NativeInt;
  var DaclPresent: BOOL;
  var Dacl: NativeInt;
  var DaclDefaulted: BOOL): BOOL;
  external 'GetSecurityDescriptorDacl@advapi32.dll stdcall';

function WindowsGetSecurityDescriptorOwner(
  SecurityDescriptor: NativeInt;
  var Owner: NativeInt;
  var OwnerDefaulted: BOOL): BOOL;
  external 'GetSecurityDescriptorOwner@advapi32.dll stdcall';

function WindowsSetNamedSecurityInfo(
  ObjectName: String;
  ObjectType: DWORD;
  SecurityInfo: DWORD;
  Owner: NativeInt;
  Group: NativeInt;
  Dacl: NativeInt;
  Sacl: NativeInt): DWORD;
  external 'SetNamedSecurityInfoW@advapi32.dll stdcall';

function WindowsLocalFree(Memory: NativeInt): NativeInt;
  external 'LocalFree@kernel32.dll stdcall';

function WindowsGetTickCount64(): Int64;
  external 'GetTickCount64@kernel32.dll stdcall';

function QueryPreviousInstall(RootKey: HKEY; var InstallDirectory: String; var Uninstaller: String): Boolean;
var
  HasInstallDirectory: Boolean;
  HasUninstaller: Boolean;
begin
  HasInstallDirectory := RegQueryStringValue(RootKey, InstallRegistryKey, 'InstallLocation', InstallDirectory);
  HasUninstaller := RegQueryStringValue(RootKey, InstallRegistryKey, 'UninstallString', Uninstaller);
  Result := HasInstallDirectory or HasUninstaller;
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
  begin
    if CompareText(ParamStr(Index), Parameter) = 0 then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  HasPreviousMachineInstall := QueryPreviousInstall(HKEY_LOCAL_MACHINE, PreviousMachineInstallDirectory, PreviousMachineUninstaller);
  RegQueryDWordValue(HKEY_LOCAL_MACHINE, InstallRegistryKey, 'InstallLayoutVersion', PreviousMachineLayoutVersion);
  HasPreviousUserInstall := QueryPreviousInstall(HKEY_CURRENT_USER, PreviousUserInstallDirectory, PreviousUserUninstaller);
  if HasPreviousMachineInstall and HasPreviousUserInstall and
     (CompareText(PreviousUserUninstaller, PreviousMachineUninstaller) = 0) then
    HasPreviousUserInstall := False;
  Result := True;
end;

function GetDefaultInstallPath(Param: String): String;
begin
  if (PreviousMachineLayoutVersion = 2) and (PreviousMachineInstallDirectory <> '') then
  begin
    Result := RemoveBackslashUnlessRoot(PreviousMachineInstallDirectory);
    exit;
  end;

  if WindowsGetDriveType('D:\') = DRIVE_FIXED then
    Result := 'D:\Program Files\{#AppName}'
  else
    Result := ExpandConstant('{autopf}\{#AppName}');
end;

function IsSameDirectory(FirstPath: String; SecondPath: String): Boolean;
begin
  Result := (FirstPath <> '') and (SecondPath <> '') and
    (CompareText(RemoveBackslashUnlessRoot(FirstPath), RemoveBackslashUnlessRoot(SecondPath)) = 0);
end;

function IsSameFile(FirstPath: String; SecondPath: String): Boolean;
begin
  Result := (FirstPath <> '') and (SecondPath <> '') and
    (CompareText(ExpandFileName(RemoveQuotes(FirstPath)), ExpandFileName(RemoveQuotes(SecondPath))) = 0);
end;

function InspectDirectory(DirectoryPath: String; var ErrorDetail: String): TDirectoryInspection;
var
  Attributes: Cardinal;
  ErrorCode: LongInt;
  FindRec: TFindRec;
  HasEntry: Boolean;
begin
  ErrorDetail := '';
  Attributes := WindowsGetFileAttributes(DirectoryPath);
  if Attributes = EverywhereInvalidFileAttributes then
  begin
    ErrorCode := DLLGetLastError;
    if (ErrorCode = ERROR_FILE_NOT_FOUND) or (ErrorCode = ERROR_PATH_NOT_FOUND) then
      Result := diMissing
    else
    begin
      ErrorDetail := Format('GetFileAttributes failed for "%s" with error %d.', [DirectoryPath, ErrorCode]);
      Result := diUnavailable;
    end;
    exit;
  end;

  if (Attributes and EverywhereFileAttributeDirectory) = 0 then
  begin
    ErrorDetail := Format('The selected installation path is not a directory: %s', [DirectoryPath]);
    Result := diUnavailable;
    exit;
  end;

  HasEntry := False;
  if FindFirst(AddBackslash(DirectoryPath) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          HasEntry := True;
      until HasEntry or (not FindNext(FindRec));
    finally
      FindClose(FindRec);
    end;
  end
  else
  begin
    ErrorCode := DLLGetLastError;
    if (ErrorCode = ERROR_FILE_NOT_FOUND) or (ErrorCode = ERROR_PATH_NOT_FOUND) or
       (ErrorCode = ERROR_NO_MORE_FILES) then
    begin
      Result := diEmpty;
      exit;
    end;

    ErrorDetail := Format('Setup could not enumerate "%s": error %d.', [DirectoryPath, ErrorCode]);
    Result := diUnavailable;
    exit;
  end;

  if HasEntry then
    Result := diNotEmpty
  else
    Result := diEmpty;
end;

function ValidateInstallDirectoryBeforeMutation(
  DirectoryPath: String;
  IsRecognizedPreviousDirectory: Boolean;
  var ErrorDetail: String): Boolean;
var
  Inspection: TDirectoryInspection;
  ProbePath: String;
  WasCreated: Boolean;
begin
  Result := False;
  Inspection := InspectDirectory(DirectoryPath, ErrorDetail);
  if Inspection = diUnavailable then
    exit;
  if (Inspection = diNotEmpty) and (not IsRecognizedPreviousDirectory) then
    exit;

  WasCreated := Inspection = diMissing;
  if WasCreated and (not ForceDirectories(DirectoryPath)) then
  begin
    ErrorDetail := Format('Setup could not create the selected installation folder: %s', [DirectoryPath]);
    exit;
  end;

  ProbePath := AddBackslash(DirectoryPath) +
    Format('.everywhere-install-probe-%d-%d.tmp', [WindowsGetTickCount64(), Random(1000000)]);
  if not SaveStringToFile(ProbePath, '', False) then
  begin
    ErrorDetail := Format('Setup could not create an access probe in "%s".', [DirectoryPath]);
    if WasCreated then
      RemoveDir(DirectoryPath);
    exit;
  end;

  if not DeleteFile(ProbePath) then
  begin
    ErrorDetail := Format('Setup could not remove its access probe from "%s".', [DirectoryPath]);
    exit;
  end;

  if WasCreated and (not RemoveDir(DirectoryPath)) then
    Log(Format('Setup left the newly created empty installation folder in place: %s', [DirectoryPath]));

  Result := True;
end;

function WaitForDirectoryEmpty(DirectoryPath: String; var ErrorDetail: String): Boolean;
var
  Deadline: Int64;
  Inspection: TDirectoryInspection;
begin
  Deadline := WindowsGetTickCount64() + 5000;
  repeat
    Inspection := InspectDirectory(DirectoryPath, ErrorDetail);
    if (Inspection = diMissing) or (Inspection = diEmpty) then
    begin
      Result := True;
      exit;
    end;

    if Inspection = diUnavailable then
    begin
      Result := False;
      exit;
    end;

    Sleep(100);
  until WindowsGetTickCount64() >= Deadline;

  Inspection := InspectDirectory(DirectoryPath, ErrorDetail);
  Result := (Inspection = diMissing) or (Inspection = diEmpty);
end;

function ProtectInstallDirectory(DirectoryPath: String; var ErrorDetail: String): Boolean;
var
  Attributes: Cardinal;
  SecurityDescriptor: NativeInt;
  Owner: NativeInt;
  Dacl: NativeInt;
  DaclPresent: BOOL;
  OwnerDefaulted: BOOL;
  DaclDefaulted: BOOL;
  ErrorCode: DWORD;
begin
  Result := False;
  ErrorDetail := '';
  if not ForceDirectories(DirectoryPath) then
  begin
    ErrorDetail := Format('Windows could not create the installation folder: %s', [DirectoryPath]);
    exit;
  end;

  Attributes := WindowsGetFileAttributes(DirectoryPath);
  if Attributes = EverywhereInvalidFileAttributes then
  begin
    ErrorDetail := Format('Windows could not inspect the installation folder: %s', [DirectoryPath]);
    exit;
  end;
  if (Attributes and EverywhereFileAttributeReparsePoint) <> 0 then
  begin
    ErrorDetail := Format('The installation folder is a reparse point: %s', [DirectoryPath]);
    exit;
  end;

  SecurityDescriptor := 0;
  if not WindowsConvertStringSecurityDescriptorToSecurityDescriptor(
       'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200A9;;;BU)',
       SDDL_REVISION_1,
       SecurityDescriptor,
       0) then
  begin
    ErrorDetail := Format('Windows could not create the installation security descriptor: error %d.', [DLLGetLastError]);
    exit;
  end;

  try
    Owner := 0;
    Dacl := 0;
    DaclPresent := False;
    OwnerDefaulted := False;
    DaclDefaulted := False;
    if not WindowsGetSecurityDescriptorOwner(SecurityDescriptor, Owner, OwnerDefaulted) then
    begin
      ErrorDetail := Format('Windows could not read the installation-folder owner descriptor: error %d.', [DLLGetLastError]);
      exit;
    end;
    if not WindowsGetSecurityDescriptorDacl(SecurityDescriptor, DaclPresent, Dacl, DaclDefaulted) then
    begin
      ErrorDetail := Format('Windows could not read the installation-folder access descriptor: error %d.', [DLLGetLastError]);
      exit;
    end;

    ErrorCode := WindowsSetNamedSecurityInfo(
      RemoveBackslashUnlessRoot(DirectoryPath),
      SE_FILE_OBJECT,
      OWNER_SECURITY_INFORMATION or DACL_SECURITY_INFORMATION or PROTECTED_DACL_SECURITY_INFORMATION,
      Owner,
      0,
      Dacl,
      0);
    if ErrorCode <> 0 then
    begin
      ErrorDetail := Format('Windows could not protect the installation folder: error %d.', [ErrorCode]);
      exit;
    end;

    Result := True;
  finally
    WindowsLocalFree(SecurityDescriptor);
  end;
end;

procedure PrepareInstallDirectory();
var
  ErrorDetail: String;
begin
  if ProtectInstallDirectory(WizardDirValue, ErrorDetail) then
    exit;

  Log(Format('Installation-directory protection failed: %s', [ErrorDetail]));

  if WizardSilent then
    RaiseException(ExpandConstant('{cm:InstallDirectoryProtectionFailed}'));

  if MsgBox(
       ExpandConstant('{cm:InstallDirectoryProtectionFailed}'),
       mbError,
       MB_YESNO or MB_DEFBUTTON2) <> IDYES then
    Abort;

  Log('The user chose to continue without verified installation-directory protection.');
end;

function IsProgramFilesDirectory(DirectoryPath: String): Boolean;
var
  ProgramFilesRoot: String;
  NormalizedDirectory: String;
begin
  Result := False;
  NormalizedDirectory := AddBackslash(RemoveBackslashUnlessRoot(DirectoryPath));

  if (Length(NormalizedDirectory) < 3) or (NormalizedDirectory[2] <> ':') then
    exit;

  if WindowsGetDriveType(Copy(NormalizedDirectory, 1, 3)) <> DRIVE_FIXED then
    exit;

  ProgramFilesRoot := AddBackslash(Copy(NormalizedDirectory, 1, 3) + 'Program Files');
  Result := CompareText(Copy(NormalizedDirectory, 1, Length(ProgramFilesRoot)), ProgramFilesRoot) = 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedDirectory: String;
  ErrorDetail: String;
  IsRecognizedPreviousDirectory: Boolean;
  Inspection: TDirectoryInspection;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    exit;

  SelectedDirectory := WizardDirValue;
  IsRecognizedPreviousDirectory := IsSameDirectory(SelectedDirectory, PreviousMachineInstallDirectory) or
    IsSameDirectory(SelectedDirectory, PreviousUserInstallDirectory);
  Inspection := InspectDirectory(SelectedDirectory, ErrorDetail);
  if (Inspection = diUnavailable) or
     ((Inspection = diNotEmpty) and (not IsRecognizedPreviousDirectory)) then
  begin
    Log(Format('Installation-directory validation failed: %s', [ErrorDetail]));
    if ErrorDetail = '' then
      MsgBox(ExpandConstant('{cm:InstallDirectoryNotEmpty}'), mbError, MB_OK)
    else
      MsgBox(ExpandConstant('{cm:InstallDirectoryUnavailable}'), mbError, MB_OK);
    Result := False;
    exit;
  end;

  if (not IsProgramFilesDirectory(SelectedDirectory)) and
     (not IsSameDirectory(SelectedDirectory, ApprovedUnprotectedDirectory)) then
  begin
    if WizardSilent then
    begin
      Log(ExpandConstant('{cm:UnprotectedInstallDirectory}'));
      ApprovedUnprotectedDirectory := SelectedDirectory;
      exit;
    end;

    if MsgBox(ExpandConstant('{cm:UnprotectedInstallDirectory}'), mbConfirmation, MB_YESNO) <> IDYES then
    begin
      Result := False;
      exit;
    end;

    ApprovedUnprotectedDirectory := SelectedDirectory;
  end;
end;

function ValidatePreviousUninstaller(IsRegistered: Boolean; Uninstaller: String): Boolean;
var
  UninstallerPath: String;
begin
  if not IsRegistered then
  begin
    Result := True;
    exit;
  end;

  UninstallerPath := ExtractCommandExecutable(Uninstaller);
  Result := (UninstallerPath <> '') and FileExists(UninstallerPath);
end;

function RunPreviousUninstaller(IsRegistered: Boolean; Uninstaller: String; IsUserInstall: Boolean; var ResultCode: Integer): Boolean;
var
  UninstallerPath: String;
  Parameters: String;
  ExecutionContext: String;
begin
  if not IsRegistered then
  begin
    Result := True;
    exit;
  end;

  UninstallerPath := ExtractCommandExecutable(Uninstaller);
  Parameters := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG';
  if ShouldAllowIncompletePreviousCleanup then
    Parameters := Parameters + ' /ALLOWINCOMPLETECLEANUP';
  if IsUserInstall then
    ExecutionContext := 'original user'
  else
    ExecutionContext := 'elevated installer';
  Log(Format('Running previous Everywhere uninstaller: %s (%s)', [UninstallerPath, ExecutionContext]));
  if IsUserInstall then
    Result := ExecAsOriginalUser(UninstallerPath, Parameters, ExtractFileDir(UninstallerPath), SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    Result := Exec(UninstallerPath, Parameters, ExtractFileDir(UninstallerPath), SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Result := Result and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorDetail: String;
  IsRecognizedPreviousDirectory: Boolean;
begin
  Result := '';

  if not ValidatePreviousUninstaller(HasPreviousMachineInstall, PreviousMachineUninstaller) then
  begin
    Result := ExpandConstant('{cm:PreviousUninstallerMissing}');
    exit;
  end;

  if not ValidatePreviousUninstaller(HasPreviousUserInstall, PreviousUserUninstaller) then
  begin
    Result := ExpandConstant('{cm:PreviousUninstallerMissing}');
    exit;
  end;

  IsRecognizedPreviousDirectory := IsSameDirectory(WizardDirValue, PreviousMachineInstallDirectory) or
    IsSameDirectory(WizardDirValue, PreviousUserInstallDirectory);
  if not ValidateInstallDirectoryBeforeMutation(WizardDirValue, IsRecognizedPreviousDirectory, ErrorDetail) then
  begin
    Log(Format('Pre-install directory validation failed: %s', [ErrorDetail]));
    if ErrorDetail = '' then
      Result := ExpandConstant('{cm:InstallDirectoryNotEmpty}')
    else
      Result := ExpandConstant('{cm:InstallDirectoryUnavailable}');
  end;
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
begin
  Result := True;
  if not RegValueExists(RootKey, Subkey, '{#AppName}') then
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

function CleanupPreviousTasks(InstallDirectory: String; var ErrorDetail: String): Boolean;
var
  ExecutablePath: String;
begin
  if InstallDirectory = '' then
  begin
    Result := True;
    exit;
  end;

  ExecutablePath := AddBackslash(RemoveBackslashUnlessRoot(InstallDirectory)) + '{#AppExeName}';
  Result := CleanupOwnedTasks(ExecutablePath, ErrorDetail);
end;

function TryCleanupPreviousResources(var ErrorDetail: String): Boolean;
var
  ItemError: String;
begin
  ErrorDetail := '';
  Result := True;
  if not CleanupPreviousTasks(PreviousMachineInstallDirectory, ItemError) then
  begin
    AppendErrorDetail(ErrorDetail, 'machine-install tasks', ItemError);
    Result := False;
  end;
  if (not IsSameDirectory(PreviousUserInstallDirectory, PreviousMachineInstallDirectory)) and
     (not CleanupPreviousTasks(PreviousUserInstallDirectory, ItemError)) then
  begin
    AppendErrorDetail(ErrorDetail, 'user-install tasks', ItemError);
    Result := False;
  end;
  if not CleanupEverywhereAutoruns(ItemError) then
  begin
    AppendErrorDetail(ErrorDetail, 'startup entries', ItemError);
    Result := False;
  end;
end;

procedure ConfirmPreviousResourceCleanup();
var
  ErrorDetail: String;
  Response: Integer;
begin
  repeat
    if TryCleanupPreviousResources(ErrorDetail) then
      exit;

    Log(Format('Previous installation resource cleanup failed: %s', [ErrorDetail]));
    if WizardSilent then
      RaiseException(ExpandConstant('{cm:PreviousInstallCleanupFailed}'));

    Response := MsgBox(
      ExpandConstant('{cm:PreviousInstallCleanupFailed}'),
      mbError,
      MB_ABORTRETRYIGNORE or MB_DEFBUTTON1);
    if Response = IDABORT then
      Abort;
    if Response = IDIGNORE then
    begin
      ShouldAllowIncompletePreviousCleanup := True;
      Log('The user chose to continue installation with incomplete previous-resource cleanup.');
      exit;
    end;
  until False;
end;

procedure RemovePreviousInstallation();
var
  ResultCode: Integer;
  ErrorDetail: String;
begin
  ConfirmPreviousResourceCleanup();

  ResultCode := -1;
  if not RunPreviousUninstaller(HasPreviousMachineInstall, PreviousMachineUninstaller, False, ResultCode) then
    RaiseException(FmtMessage(ExpandConstant('{cm:PreviousUninstallFailed}'), [IntToStr(ResultCode)]));

  ResultCode := -1;
  if not RunPreviousUninstaller(HasPreviousUserInstall, PreviousUserUninstaller, True, ResultCode) then
    RaiseException(FmtMessage(ExpandConstant('{cm:PreviousUninstallFailed}'), [IntToStr(ResultCode)]));

  if not WaitForDirectoryEmpty(WizardDirValue, ErrorDetail) then
  begin
    Log(Format('The installation directory was not ready after removing the previous version: %s', [ErrorDetail]));
    if ErrorDetail = '' then
      RaiseException(ExpandConstant('{cm:InstallDirectoryStillNotEmpty}'))
    else
      RaiseException(ExpandConstant('{cm:InstallDirectoryUnavailable}'));
  end;
end;

procedure CleanupLegacyRegistry();
var
  Identifier: String;
  UserHives: TArrayOfString;
  UserKey: String;
  Index: Integer;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Everywhere', 'Identifier', Identifier) and (Identifier = 'D66EA41B-8DEB-4E5A-9D32-AB4F8305F664') then
  begin
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Everywhere');
  end;

  if not RegGetSubkeyNames(HKEY_USERS, '', UserHives) then
    exit;

  for Index := 0 to GetArrayLength(UserHives) - 1 do
  begin
    UserKey := UserHives[Index] + '\Software\Microsoft\Windows\CurrentVersion\Uninstall\Everywhere';
    if RegQueryStringValue(HKEY_USERS, UserKey, 'Identifier', Identifier) and (Identifier = 'D66EA41B-8DEB-4E5A-9D32-AB4F8305F664') then
      RegDeleteKeyIncludingSubkeys(HKEY_USERS, UserKey);
  end;
end;

function RegisterInstalledLayout(): Boolean;
var
  RegisteredLayoutVersion: Cardinal;
begin
  Result := RegWriteDWordValue(HKEY_LOCAL_MACHINE, InstallRegistryKey, 'InstallLayoutVersion', 2) and
    RegQueryDWordValue(HKEY_LOCAL_MACHINE, InstallRegistryKey, 'InstallLayoutVersion', RegisteredLayoutVersion) and
    (RegisteredLayoutVersion = 2);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    RemovePreviousInstallation();
    PrepareInstallDirectory();
    exit;
  end;

  if CurStep <> ssPostInstall then
    exit;

  { Inno recreates its uninstall key while saving uninstall information, so the layout marker must be written here. }
  if not RegisterInstalledLayout() then
  begin
    Log('Failed to write or verify InstallLayoutVersion after Inno registered the installation.');
    if not WizardSilent then
      MsgBox(ExpandConstant('{cm:InstallIdentityRegistrationFailed}'), mbError, MB_OK);
    exit;
  end;

  CleanupLegacyRegistry();
  ResultCode := -1;
  if (not Exec(ExpandConstant('{app}\{#AppExeName}'), '--hosts-control install --replace-existing', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    Log(Format('Hosts task installation failed with exit code %d.', [ResultCode]));
    if not WizardSilent then
      MsgBox(ExpandConstant('{cm:HostsTaskInstallFailed}'), mbError, MB_OK);
  end;
end;

function TryCleanupCurrentResources(var ErrorDetail: String): Boolean;
var
  ExecutablePath: String;
  ResultCode: Integer;
  IsControllerCleanupComplete: Boolean;
  IsTaskCleanupComplete: Boolean;
  IsAutorunCleanupComplete: Boolean;
  ItemError: String;
begin
  ErrorDetail := '';
  ExecutablePath := ExpandConstant('{app}\{#AppExeName}');

  if FileExists(ExecutablePath) then
  begin
    ResultCode := -1;
    if (not Exec(ExecutablePath, '--hosts-control stop', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
      Log(Format('Hosts stop was not confirmed before uninstall; exit code %d. Continuing with owned resource cleanup.', [ResultCode]));

    ResultCode := -1;
    IsControllerCleanupComplete := Exec(ExecutablePath, '--hosts-control uninstall', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
    if not IsControllerCleanupComplete then
      Log(Format('Hosts controller cleanup failed with exit code %d; using Task Scheduler COM fallback.', [ResultCode]));
  end
  else
  begin
    Log('Everywhere.exe is unavailable; using Task Scheduler COM fallback.');
    IsControllerCleanupComplete := False;
  end;

  ErrorDetail := '';
  IsTaskCleanupComplete := IsControllerCleanupComplete;
  if not IsTaskCleanupComplete then
  begin
    IsTaskCleanupComplete := CleanupOwnedTasks(ExecutablePath, ItemError);
    if not IsTaskCleanupComplete then
      AppendErrorDetail(ErrorDetail, 'scheduled tasks', ItemError);
  end;

  IsAutorunCleanupComplete := CleanupEverywhereAutoruns(ItemError);
  if not IsAutorunCleanupComplete then
    AppendErrorDetail(ErrorDetail, 'startup entries', ItemError);

  Result := IsTaskCleanupComplete and IsAutorunCleanupComplete;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ErrorDetail: String;
  Response: Integer;
begin
  if CurUninstallStep <> usUninstall then
    exit;

  repeat
    if TryCleanupCurrentResources(ErrorDetail) then
      exit;

    Log(Format('Uninstall resource cleanup failed: %s', [ErrorDetail]));
    if UninstallSilent then
    begin
      if HasCommandLineParameter('/ALLOWINCOMPLETECLEANUP') then
      begin
        Log('Continuing silent uninstall because the calling Setup explicitly allowed incomplete cleanup.');
        exit;
      end;

      Abort;
    end;

    Response := MsgBox(
      ExpandConstant('{cm:UninstallCleanupFailed}'),
      mbError,
      MB_ABORTRETRYIGNORE or MB_DEFBUTTON1);
    if Response = IDABORT then
      Abort;
    if Response = IDIGNORE then
    begin
      Log('The user chose to continue uninstalling with incomplete background-service cleanup.');
      exit;
    end;
  until False;

end;

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
