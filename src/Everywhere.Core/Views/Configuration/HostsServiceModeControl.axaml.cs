using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Everywhere.Common;
using Everywhere.ProcessIsolation.Hosting;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

/// <summary>Presentation state for the installed Hosts service-mode integration.</summary>
/// <param name="State">Current configuration state, or <see langword="null"/> while querying.</param>
/// <param name="ToolTip">Optional localized tooltip.</param>
public sealed record HostsServiceModePresentation(HostsServiceModeConfigurationState? State, IDynamicLocaleKey ToolTip)
{
    public bool IsWarning => State is HostsServiceModeConfigurationState.OtherExecutable;

    public bool IsError => State is HostsServiceModeConfigurationState.Invalid or HostsServiceModeConfigurationState.Unavailable;

    public bool RequiresAttention => IsWarning || IsError;
}

/// <summary>Installs or removes the shared elevated Hosts launcher.</summary>
public class HostsServiceModeControl(
    HostProcessCoordinator coordinator,
    IHostsServiceModeManager serviceModeManager,
    ILogger<HostsServiceModeControl> logger
) : TemplatedControl
{
    public static readonly DirectProperty<HostsServiceModeControl, HostsServiceModePresentation> StatusProperty =
        AvaloniaProperty.RegisterDirect<HostsServiceModeControl, HostsServiceModePresentation>(nameof(Status), control => control.Status);

    public static readonly DirectProperty<HostsServiceModeControl, bool> IsInstalledProperty =
        AvaloniaProperty.RegisterDirect<HostsServiceModeControl, bool>(
            nameof(IsInstalled),
            control => control.IsInstalled,
            (control, value) => control.SetInstalled(value));

    public static readonly DirectProperty<HostsServiceModeControl, bool> IsOperationRunningProperty =
        AvaloniaProperty.RegisterDirect<HostsServiceModeControl, bool>(nameof(IsOperationRunning), control => control.IsOperationRunning);

    public HostsServiceModePresentation Status
    {
        get;
        private set => SetAndRaise(StatusProperty, ref field, value);
    } = new(null, DirectLocaleKey.Empty);

    public bool IsInstalled
    {
        get => _isInstalled;
        set => SetInstalled(value);
    }

    public bool IsOperationRunning
    {
        get;
        private set => SetAndRaise(IsOperationRunningProperty, ref field, value);
    } = true;

    private bool _isInstalled;
    private bool _isAttached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        InitializeAsync().Detach(logger.ToExceptionHandler());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await RefreshStatusAsync();
        }
        finally
        {
            IsOperationRunning = false;
        }
    }

    private void SetInstalled(bool value)
    {
        if (_isInstalled == value || IsOperationRunning)
        {
            return;
        }

        ChangeInstallationAsync(value).Detach(logger.ToExceptionHandler());
    }

    private async Task ChangeInstallationAsync(bool shouldInstall)
    {
        IsOperationRunning = true;
        try
        {
            var result = shouldInstall ? await InstallAsync() : await UninstallAsync();
            logger.LogInformation(
                "Hosts service-mode change completed: install={ShouldInstall}, outcome={Outcome}, detail={Detail}.",
                shouldInstall,
                result.Outcome,
                result.DiagnosticDetail);

            if (result.Outcome is HostsControlPlatformOutcome.Succeeded)
            {
                await coordinator.RestartHostsAsync();
            }
            else if (result.Outcome is not HostsControlPlatformOutcome.Cancelled)
            {
                var message = shouldInstall ?
                    LocaleResolver.HostsStatusControl_ServiceModeConfigurationFailed :
                    LocaleResolver.HostsStatusControl_ServiceModeRemovalFailed;
                ToastManager.Error(LocaleResolver.Common_Error, message);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to change Hosts service-mode installation to {ShouldInstall}.", shouldInstall);
            ToastManager.Error(LocaleResolver.Common_Error, HandledSystemException.Handle(exception).GetFriendlyMessage().ToString());
        }
        finally
        {
            await RefreshStatusAsync();
            IsOperationRunning = false;
        }
    }

    private async Task<HostsControlPlatformResult> InstallAsync()
    {
        var status = await GetStatusAsync();
        ApplyStatus(status);
        LogStatus(status);
        if (status.State is HostsServiceModeConfigurationState.CurrentExecutable)
        {
            return HostsControlPlatformResult.Success("Service mode is already installed for this executable.");
        }

        if (status.State is HostsServiceModeConfigurationState.Unavailable)
        {
            return HostsControlPlatformResult.Failure(status.DiagnosticDetail ?? "The service-mode configuration is unavailable.");
        }

        var assessment = serviceModeManager.AssessEnvironment();
        if (!await ConfirmInstallationAsync(status, assessment))
        {
            return HostsControlPlatformResult.Cancelled("The user cancelled service-mode installation.");
        }

        var shouldReplaceExisting = status.State is HostsServiceModeConfigurationState.OtherExecutable or HostsServiceModeConfigurationState.Invalid;
        logger.LogInformation(
            "Requesting Hosts service-mode installation: replaceExisting={ShouldReplaceExisting}, authorizePortable={ShouldAuthorizePortable}, previousOwner={PreviousOwner}, environment={EnvironmentDetail}.",
            shouldReplaceExisting,
            assessment.RequiresPortableAuthorization,
            status.ConfiguredExecutablePath,
            assessment.DiagnosticDetail);
        return await serviceModeManager.RequestInstallAsync(shouldReplaceExisting, assessment.RequiresPortableAuthorization);
    }

    private async Task<HostsControlPlatformResult> UninstallAsync()
    {
        var status = await GetStatusAsync();
        ApplyStatus(status);
        LogStatus(status);
        return status.State switch
        {
            HostsServiceModeConfigurationState.NotConfigured => HostsControlPlatformResult.Success("Service mode is not installed."),
            HostsServiceModeConfigurationState.CurrentExecutable => await serviceModeManager.RequestUninstallAsync(),
            HostsServiceModeConfigurationState.OtherExecutable or HostsServiceModeConfigurationState.Invalid =>
                HostsControlPlatformResult.OwnershipConflict("The service-mode integration no longer belongs to this executable."),
            HostsServiceModeConfigurationState.Unavailable =>
                HostsControlPlatformResult.Failure(status.DiagnosticDetail ?? "The service-mode configuration is unavailable."),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, null)
        };
    }

    private async Task<bool> ConfirmInstallationAsync(HostsServiceModeStatus status, HostsServiceModeEnvironmentAssessment assessment)
    {
        var messages = new List<string>();
        if (status.State is HostsServiceModeConfigurationState.OtherExecutable)
        {
            messages.Add(
                string.Format(
                    LocaleResolver.HostsStatusControl_ServiceModeOwnershipConflict_Message,
                    status.ConfiguredExecutablePath ?? LocaleResolver.Common_Unknown,
                    Environment.ProcessPath ?? LocaleResolver.Common_Unknown));
        }
        else if (status.State is HostsServiceModeConfigurationState.Invalid)
        {
            messages.Add(LocaleResolver.HostsStatusControl_ServiceModeInvalidOwnership_Message);
        }

        if (assessment.RequiresWarning)
        {
            messages.Add(LocaleResolver.HostsStatusControl_ServiceModeUnsafeEnvironment_Message);
        }

        if (messages.Count == 0)
        {
            return true;
        }

        messages.Add(LocaleResolver.HostsStatusControl_ServiceModeConfirmation_Footer);
        var result = await DialogManager
            .CreateDialog(
                string.Join("\n\n", messages),
                LocaleResolver.HostsStatusControl_ServiceModeConfirmation_Title,
                TopLevel.GetTopLevel(this))
            .WithPrimaryButton(LocaleResolver.HostsStatusControl_ServiceModeConfirmation_Continue)
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync();
        return result is DialogResult.Primary;
    }

    private async Task RefreshStatusAsync()
    {
        var status = await GetStatusAsync();
        if (_isAttached)
        {
            ApplyStatus(status);
        }
    }

    private async Task<HostsServiceModeStatus> GetStatusAsync()
    {
        try
        {
            return await Task.Run(serviceModeManager.GetStatus);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to query Hosts service-mode configuration.");
            return new HostsServiceModeStatus(HostsServiceModeConfigurationState.Unavailable, DiagnosticDetail: exception.Message);
        }
    }

    private void ApplyStatus(HostsServiceModeStatus status)
    {
        SetAndRaise(IsInstalledProperty, ref _isInstalled, status.State is HostsServiceModeConfigurationState.CurrentExecutable);
        Status = CreatePresentation(status);
    }

    private void LogStatus(HostsServiceModeStatus status) =>
        logger.LogInformation(
            "Hosts service-mode configuration: state={State}, owner={Owner}, lastResult={LastTaskResult}, detail={Detail}.",
            status.State,
            status.ConfiguredExecutablePath,
            status.LastTaskResult,
            status.DiagnosticDetail);

    private static HostsServiceModePresentation CreatePresentation(HostsServiceModeStatus status)
    {
        var statusTextKey = status.State switch
        {
            HostsServiceModeConfigurationState.NotConfigured => LocaleKey.HostsStatusControl_ServiceModeNotConfigured,
            HostsServiceModeConfigurationState.CurrentExecutable => LocaleKey.HostsStatusControl_ServiceModeCurrentExecutable,
            HostsServiceModeConfigurationState.OtherExecutable => LocaleKey.HostsStatusControl_ServiceModeOtherExecutable,
            HostsServiceModeConfigurationState.Invalid => LocaleKey.HostsStatusControl_ServiceModeInvalid,
            HostsServiceModeConfigurationState.Unavailable => LocaleKey.HostsStatusControl_ServiceModeUnavailableStatus,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, null)
        };
        var toolTip = status.State is HostsServiceModeConfigurationState.OtherExecutable && status.ConfiguredExecutablePath is { } owner ?
            new FormattedDynamicLocaleKey(LocaleKey.HostsStatusControl_ServiceModeOtherExecutable_ToolTip, owner) :
            new DynamicLocaleKey(statusTextKey);
        return new HostsServiceModePresentation(status.State, toolTip);
    }
}