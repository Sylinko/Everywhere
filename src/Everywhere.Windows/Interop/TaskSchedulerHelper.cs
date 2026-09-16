using System.Runtime.InteropServices;
using System.Security;
using System.Xml.Linq;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.TaskScheduler;
using Everywhere.ProcessIsolation.Hosting;
using TaskScheduler = Windows.Win32.System.TaskScheduler.TaskScheduler;

namespace Everywhere.Windows.Interop;

/// <summary>Diagnostic result returned by a Task Scheduler command.</summary>
/// <param name="Outcome">Stable outcome consumed by the controller.</param>
/// <param name="DiagnosticDetail">English detail intended for logs and command-line output.</param>
public sealed record TaskSchedulerCommandResult(HostsControlPlatformOutcome Outcome, string DiagnosticDetail)
{
    public bool Succeeded => Outcome is HostsControlPlatformOutcome.Succeeded;

    public static TaskSchedulerCommandResult Success(string detail) =>
        new(HostsControlPlatformOutcome.Succeeded, detail);

    public static TaskSchedulerCommandResult Failure(string detail) =>
        new(HostsControlPlatformOutcome.Failed, detail);

    public static TaskSchedulerCommandResult Conflict(string detail) =>
        new(HostsControlPlatformOutcome.Conflict, detail);
}

public static class TaskSchedulerHelper
{
    // Ordinary users can read and execute the one fixed action but cannot alter
    // it. SYSTEM and Administrators retain full maintenance control.
    private const string TaskSecurityDescriptor = "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const uint FileNotFoundHResult = 0x80070002;
    private const uint PathNotFoundHResult = 0x80070003;

    public static HostsServiceModeStatus GetStatus(string taskName, string executablePath)
    {
        IRegisteredTask? task = null;
        try
        {
            using var connection = TaskSchedulerConnection.Connect();
            task = connection.GetTask(taskName);
            return CreateStatus(task, executablePath);
        }
        catch (Exception exception) when (IsTaskMissing(exception.HResult))
        {
            return new HostsServiceModeStatus(HostsServiceModeConfigurationState.NotConfigured);
        }
        catch (Exception exception)
        {
            return new HostsServiceModeStatus(HostsServiceModeConfigurationState.Unavailable, DiagnosticDetail: FormatException(exception));
        }
        finally
        {
            ReleaseComObject(task);
        }
    }

    public static TaskSchedulerCommandResult RunOwnedTask(string taskName, string executablePath, int desktopSessionId)
    {
        IRegisteredTask? task = null;
        IRunningTask? runningTask = null;
        try
        {
            using var connection = TaskSchedulerConnection.Connect();
            task = connection.GetTask(taskName);
            var status = CreateStatus(task, executablePath);
            if (status.State is not HostsServiceModeConfigurationState.CurrentExecutable)
            {
                return status.State is HostsServiceModeConfigurationState.OtherExecutable or HostsServiceModeConfigurationState.Invalid ?
                    TaskSchedulerCommandResult.Conflict(
                        $"Everywhere Hosts service mode belongs to another copy: {status.ConfiguredExecutablePath ?? status.DiagnosticDetail ?? "unknown owner"}.") :
                    TaskSchedulerCommandResult.Failure(
                        status.DiagnosticDetail ?? "Everywhere Hosts service mode is not configured for this executable.");
            }

            if (desktopSessionId <= 0)
            {
                return TaskSchedulerCommandResult.Failure($"The current desktop session ID is invalid: {desktopSessionId}.");
            }

            const TASK_RUN_FLAGS runFlags = TASK_RUN_FLAGS.TASK_RUN_AS_SELF | TASK_RUN_FLAGS.TASK_RUN_USE_SESSION_ID;
            task.RunEx(DBNull.Value, (int)runFlags, desktopSessionId, default, out runningTask);
            string engineDetail;
            try
            {
                engineDetail = $"engine PID={runningTask.EnginePID}";
            }
            catch (Exception exception)
            {
                engineDetail = $"engine PID unavailable: {FormatException(exception)}";
            }

            return TaskSchedulerCommandResult.Success(
                $"Task Scheduler accepted the Everywhere Hosts launch for session {desktopSessionId}; {engineDetail}.");
        }
        catch (Exception exception)
        {
            return TaskSchedulerCommandResult.Failure(
                $"Task Scheduler could not start Everywhere Hosts: {FormatException(exception)}");
        }
        finally
        {
            ReleaseComObject(runningTask);
            ReleaseComObject(task);
        }
    }

    public static TaskSchedulerCommandResult CreateOrUpdateHostsTask(string taskName, string executablePath)
    {
        var xmlContent = CreateHostsTaskXml(taskName, executablePath);

        IRegisteredTask? task = null;
        try
        {
            using var connection = TaskSchedulerConnection.Connect();
            using var taskNameValue = new BStr(taskName);
            using var xmlValue = new BStr(xmlContent);
            connection.RootFolder.RegisterTask(
                taskNameValue.Value,
                xmlValue.Value,
                (int)TASK_CREATION.TASK_CREATE_OR_UPDATE,
                AdministratorsSid,
                Type.Missing,
                TASK_LOGON_TYPE.TASK_LOGON_GROUP,
                TaskSecurityDescriptor,
                out task);
            return TaskSchedulerCommandResult.Success("Everywhere Hosts task was installed or repaired.");
        }
        catch (Exception exception)
        {
            return TaskSchedulerCommandResult.Failure($"Task Scheduler could not install Everywhere Hosts: {FormatException(exception)}");
        }
        finally
        {
            ReleaseComObject(task);
        }
    }

    public static TaskSchedulerCommandResult ValidateHostsTaskDefinition(string taskName, string executablePath)
    {
        ITaskDefinition? definition = null;
        try
        {
            using var connection = TaskSchedulerConnection.Connect();
            definition = connection.CreateTaskDefinition();
            using var xmlValue = new BStr(CreateHostsTaskXml(taskName, executablePath));
            definition.XmlText = xmlValue.Value;
            return TaskSchedulerCommandResult.Success("Everywhere Hosts task definition is valid.");
        }
        catch (Exception exception)
        {
            return TaskSchedulerCommandResult.Failure($"Task Scheduler rejected the Everywhere Hosts task definition: {FormatException(exception)}");
        }
        finally
        {
            ReleaseComObject(definition);
        }
    }

    private static string CreateHostsTaskXml(string taskName, string executablePath)
    {
        var escapedTaskName = SecurityElement.Escape($"\\{taskName}");
        var escapedExecutablePath = SecurityElement.Escape(executablePath);
        var workingDirectory = SecurityElement.Escape(Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory);
        return
            $"""
             <?xml version="1.0" encoding="UTF-16"?>
             <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
               <RegistrationInfo>
                 <Description>Starts the elevated Everywhere Input and Automation Hosts.</Description>
                 <URI>{escapedTaskName}</URI>
               </RegistrationInfo>
               <Principals>
                 <Principal id="Hosts">
                   <GroupId>{AdministratorsSid}</GroupId>
                   <RunLevel>HighestAvailable</RunLevel>
                 </Principal>
               </Principals>
               <Settings>
                 <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                 <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                 <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                 <AllowHardTerminate>true</AllowHardTerminate>
                 <StartWhenAvailable>false</StartWhenAvailable>
                 <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                 <AllowStartOnDemand>true</AllowStartOnDemand>
                 <Enabled>true</Enabled>
                 <Hidden>false</Hidden>
                 <RunOnlyIfIdle>false</RunOnlyIfIdle>
                 <WakeToRun>false</WakeToRun>
                 <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
                 <Priority>7</Priority>
               </Settings>
               <Actions Context="Hosts">
                 <Exec>
                   <Command>{escapedExecutablePath}</Command>
                   <Arguments>--hosts-control launch</Arguments>
                   <WorkingDirectory>{workingDirectory}</WorkingDirectory>
                 </Exec>
               </Actions>
             </Task>
             """;
    }

    public static TaskSchedulerCommandResult DeleteOwnedTask(string taskName, string executablePath)
    {
        IRegisteredTask? task = null;
        try
        {
            using var connection = TaskSchedulerConnection.Connect();
            task = connection.GetTask(taskName);
            var status = CreateStatus(task, executablePath);
            if (status.State is not HostsServiceModeConfigurationState.CurrentExecutable)
            {
                return status.State is HostsServiceModeConfigurationState.OtherExecutable or HostsServiceModeConfigurationState.Invalid ?
                    TaskSchedulerCommandResult.Conflict(
                        $"Everywhere Hosts task was preserved because it belongs to another copy: {status.ConfiguredExecutablePath ?? status.DiagnosticDetail ?? "unknown owner"}.") :
                    TaskSchedulerCommandResult.Failure(
                        status.DiagnosticDetail ?? "Everywhere Hosts task ownership could not be verified.");
            }

            connection.DeleteTask(taskName);
            return TaskSchedulerCommandResult.Success("Everywhere Hosts task was removed.");
        }
        catch (Exception exception) when (IsTaskMissing(exception.HResult))
        {
            return TaskSchedulerCommandResult.Success("Everywhere Hosts task was not installed.");
        }
        catch (Exception exception)
        {
            return TaskSchedulerCommandResult.Failure($"Task Scheduler could not remove Everywhere Hosts: {FormatException(exception)}");
        }
        finally
        {
            ReleaseComObject(task);
        }
    }

    private static HostsServiceModeStatus CreateStatus(IRegisteredTask task, string executablePath)
    {
        string xml;
        try
        {
            xml = ReadAndFree(task.Xml);
        }
        catch (Exception exception)
        {
            return new HostsServiceModeStatus(
                HostsServiceModeConfigurationState.Invalid,
                DiagnosticDetail: $"The registered task XML could not be read: {FormatException(exception)}");
        }

        string? configuredExecutablePath;
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            configuredExecutablePath = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Command")?.Value;
        }
        catch (Exception exception)
        {
            return new HostsServiceModeStatus(
                HostsServiceModeConfigurationState.Invalid,
                DiagnosticDetail: $"The registered task XML is invalid: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(configuredExecutablePath))
        {
            return new HostsServiceModeStatus(
                HostsServiceModeConfigurationState.Invalid,
                DiagnosticDetail: "The registered task does not contain an executable action.");
        }

        var state = AreSamePath(configuredExecutablePath, executablePath) ?
            HostsServiceModeConfigurationState.CurrentExecutable :
            HostsServiceModeConfigurationState.OtherExecutable;
        return new HostsServiceModeStatus(state, configuredExecutablePath, task.LastTaskResult);
    }

    private static string ReadAndFree(BSTR value)
    {
        try
        {
            return value.ToString();
        }
        finally
        {
            PInvoke.SysFreeString(value);
        }
    }

    private static bool AreSamePath(string firstPath, string secondPath)
    {
        try
        {
            return string.Equals(Path.GetFullPath(firstPath), Path.GetFullPath(secondPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsTaskMissing(int hResult) => unchecked((uint)hResult) is FileNotFoundHResult or PathNotFoundHResult;

    private static string FormatException(Exception exception) => $"{exception.Message} (0x{exception.HResult:X8})";

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed class TaskSchedulerConnection : IDisposable
    {
        public ITaskFolder RootFolder { get; }

        private readonly ITaskService _service;

        private TaskSchedulerConnection(ITaskService service, ITaskFolder rootFolder)
        {
            _service = service;
            RootFolder = rootFolder;
        }

        public static TaskSchedulerConnection Connect()
        {
            // ReSharper disable once SuspiciousTypeConversion.Global
            var service = (ITaskService)new TaskScheduler();
            try
            {
                service.Connect(Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                using var rootPath = new BStr("\\");
                service.GetFolder(rootPath.Value, out var rootFolder);
                return new TaskSchedulerConnection(service, rootFolder);
            }
            catch
            {
                ReleaseComObject(service);
                throw;
            }
        }

        public IRegisteredTask GetTask(string taskName)
        {
            using var taskNameValue = new BStr(taskName);
            RootFolder.GetTask(taskNameValue.Value, out var task);
            return task;
        }

        public ITaskDefinition CreateTaskDefinition()
        {
            _service.NewTask(0, out var definition);
            return definition;
        }

        public void DeleteTask(string taskName)
        {
            using var taskNameValue = new BStr(taskName);
            RootFolder.DeleteTask(taskNameValue.Value, 0);
        }

        public void Dispose()
        {
            ReleaseComObject(RootFolder);
            ReleaseComObject(_service);
        }
    }

    private sealed class BStr(string value) : IDisposable
    {
        public BSTR Value => (BSTR)_value;

        private IntPtr _value = Marshal.StringToBSTR(value);

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _value, IntPtr.Zero);
            if (value != IntPtr.Zero)
            {
                Marshal.FreeBSTR(value);
            }
        }
    }
}