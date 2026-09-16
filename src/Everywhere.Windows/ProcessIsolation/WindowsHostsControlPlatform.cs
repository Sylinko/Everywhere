using System.ComponentModel;
using System.Diagnostics;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.Windows.Interop;

namespace Everywhere.Windows.ProcessIsolation;

/// <summary>
/// Implements Windows Task Scheduler integration for both the early controller
/// process and Main's service-mode management UI.
/// </summary>
internal sealed class WindowsHostsControlPlatform : IHostsControlPlatform, IHostsServiceModeManager
{
    private const string TaskName = "Everywhere Hosts";
    private const string LegacyElevatedMainTaskName = "Everywhere";

    /// <inheritdoc />
    public bool IsDirectLaunchEquivalentToServiceMode => ProcessSecurity.HasAdministrativeToken;

    /// <inheritdoc />
    public HostsControlPlatformResult StartServiceMode(int desktopSessionId)
    {
        var executablePath = GetExecutablePath();
        if (executablePath is null)
        {
            return HostsControlPlatformResult.Failure("Everywhere Hosts Control could not resolve the current executable path.");
        }

        return ToPlatformResult(TaskSchedulerHelper.RunOwnedTask(TaskName, executablePath, desktopSessionId));
    }

    /// <inheritdoc />
    public HostsControlPlatformResult InstallServiceMode(HostsControlCommand command)
    {
        var executablePath = GetExecutablePath();
        if (executablePath is null)
        {
            return HostsControlPlatformResult.Failure("Everywhere Hosts Control could not resolve the current executable path.");
        }

        var status = TaskSchedulerHelper.GetStatus(TaskName, executablePath);
        if (status.State is HostsServiceModeConfigurationState.Unavailable)
        {
            return HostsControlPlatformResult.Failure(status.DiagnosticDetail ?? "The existing Everywhere Hosts task could not be queried.");
        }

        if (status.State is HostsServiceModeConfigurationState.OtherExecutable or HostsServiceModeConfigurationState.Invalid &&
            !command.ShouldReplaceExisting)
        {
            return HostsControlPlatformResult.OwnershipConflict(
                $"Everywhere Hosts service mode is already configured by another copy: {status.ConfiguredExecutablePath ?? status.DiagnosticDetail ?? "unknown owner"}. Use --replace-existing only after the caller confirms the change.");
        }

        var isInstalled = InstallationIdentity.IsCurrentMachineInstallation(executablePath);
        if (!isInstalled && !command.ShouldAuthorizePortable)
        {
            return HostsControlPlatformResult.OwnershipConflict(
                "This executable is not the registered machine installation. Portable service mode requires an explicit environment check and --authorize-portable.");
        }

        var taskResult = TaskSchedulerHelper.CreateOrUpdateHostsTask(TaskName, executablePath);
        if (!taskResult.Succeeded)
        {
            return ToPlatformResult(taskResult);
        }

        var verification = TaskSchedulerHelper.GetStatus(TaskName, executablePath);
        if (verification.State is not HostsServiceModeConfigurationState.CurrentExecutable)
        {
            return HostsControlPlatformResult.Failure(
                verification.DiagnosticDetail ?? "The Everywhere Hosts task was written but its executable owner could not be verified.");
        }

        var legacyCleanup = TaskSchedulerHelper.DeleteOwnedTask(LegacyElevatedMainTaskName, executablePath);
        var previousOwner = status.State is HostsServiceModeConfigurationState.OtherExecutable ? status.ConfiguredExecutablePath : null;
        var detail = previousOwner is null ? taskResult.DiagnosticDetail : $"{taskResult.DiagnosticDetail} Previous owner: {previousOwner}.";
        if (legacyCleanup is { Succeeded: false, Outcome: not HostsControlPlatformOutcome.Conflict })
        {
            detail = $"{detail} Legacy task cleanup failed: {legacyCleanup.DiagnosticDetail}";
        }

        return HostsControlPlatformResult.Success(detail);
    }

    /// <inheritdoc />
    public HostsControlPlatformResult UninstallServiceMode()
    {
        var executablePath = GetExecutablePath();
        if (executablePath is null)
        {
            return HostsControlPlatformResult.Failure("Everywhere Hosts Control could not resolve the current executable path.");
        }

        var result = TaskSchedulerHelper.DeleteOwnedTask(TaskName, executablePath);
        if (!result.Succeeded)
        {
            return ToPlatformResult(result);
        }

        var legacyCleanup = TaskSchedulerHelper.DeleteOwnedTask(LegacyElevatedMainTaskName, executablePath);
        return legacyCleanup is { Succeeded: false, Outcome: not HostsControlPlatformOutcome.Conflict } ?
            HostsControlPlatformResult.Failure($"{result.DiagnosticDetail} Legacy task cleanup failed: {legacyCleanup.DiagnosticDetail}") :
            HostsControlPlatformResult.Success(result.DiagnosticDetail);
    }

    /// <inheritdoc />
    public HostsServiceModeStatus GetStatus()
    {
        var executablePath = GetExecutablePath();
        return executablePath is null ?
            new HostsServiceModeStatus(
                HostsServiceModeConfigurationState.Unavailable,
                DiagnosticDetail: "The current executable path is unavailable.") :
            TaskSchedulerHelper.GetStatus(TaskName, executablePath);
    }

    /// <inheritdoc />
    public HostsServiceModeEnvironmentAssessment AssessEnvironment()
    {
        var executablePath = GetExecutablePath();
        return executablePath is null ?
            new HostsServiceModeEnvironmentAssessment(true, true, "The current executable path is unavailable.") :
            InstallationSecurity.AssessPortableEnvironment(executablePath);
    }

    /// <inheritdoc />
    public async Task<HostsControlPlatformResult> RequestInstallAsync(
        bool shouldReplaceExisting,
        bool shouldAuthorizePortable,
        CancellationToken cancellationToken = default)
    {
        var executablePath = GetExecutablePath();
        if (executablePath is null)
        {
            return HostsControlPlatformResult.Failure("The current executable path is unavailable.");
        }

        var validation = TaskSchedulerHelper.ValidateHostsTaskDefinition(TaskName, executablePath);
        if (!validation.Succeeded)
        {
            return ToPlatformResult(validation);
        }

        var arguments = new List<string> { "--hosts-control", "install" };
        if (shouldReplaceExisting)
        {
            arguments.Add("--replace-existing");
        }

        if (shouldAuthorizePortable)
        {
            arguments.Add("--authorize-portable");
        }

        return await RunElevatedControllerAsync(executablePath, arguments, "installed or repaired", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HostsControlPlatformResult> RequestUninstallAsync(CancellationToken cancellationToken = default)
    {
        var executablePath = GetExecutablePath();
        if (executablePath is null)
        {
            return HostsControlPlatformResult.Failure("The current executable path is unavailable.");
        }

        return await RunElevatedControllerAsync(
                executablePath,
                ["--hosts-control", "uninstall"],
                "removed",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<HostsControlPlatformResult> RunElevatedControllerAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string successDescription,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return HostsControlPlatformResult.Failure("Windows did not return a process for the elevated Hosts controller.");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode switch
            {
                HostsControlExitCodes.Success => HostsControlPlatformResult.Success($"Service mode was {successDescription}."),
                HostsControlExitCodes.Conflict => HostsControlPlatformResult.OwnershipConflict(
                    "The elevated controller found a service-mode ownership or safety conflict."),
                _ => HostsControlPlatformResult.Failure($"The elevated Hosts controller exited with code {process.ExitCode}.")
            };
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return HostsControlPlatformResult.Cancelled("The service-mode elevation request was cancelled.");
        }
        catch (Exception exception)
        {
            return HostsControlPlatformResult.Failure(
                $"The elevated Hosts controller could not be started: {exception.Message} (0x{exception.HResult:X8})");
        }
    }

    private static string? GetExecutablePath()
    {
        var executablePath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetFullPath(executablePath);
    }

    private static HostsControlPlatformResult ToPlatformResult(TaskSchedulerCommandResult result) =>
        new(result.Outcome, result.DiagnosticDetail);
}