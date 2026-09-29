using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.ProcessIsolation.Hosting;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// Installs or removes the shared elevated Hosts launcher.
/// </summary>
public sealed partial class HostsServiceModeControl(
    HostProcessCoordinator coordinator,
    IHostsServiceModeManager serviceModeManager,
    ILogger<HostsServiceModeControl> logger
) : TemplatedControl
{
    public static IValueConverter StateToolTipConverter => new StateToolTipConverterImpl();

    public HostProcessCoordinator Coordinator => coordinator;

    [RelayCommand]
    private async Task ChangeInstallationAsync(ToggleSwitch sender)
    {
        var shouldInstall = sender.IsChecked ?? false;
        try
        {
            var status = await coordinator.RefreshServiceModeStatusAsync();
            sender.SetCurrentValue(ToggleButton.IsCheckedProperty, status.State == HostsServiceModeConfigurationState.CurrentExecutable);

            var request = shouldInstall ? CreateInstallRequest(status) : CreateUninstallRequest(status);
            if (request is null) return;

            await coordinator.ChangeServiceModeAsync(request);
        }
        catch (HostsServiceModeChangeException)
        {
            var message = shouldInstall ?
                LocaleResolver.HostsStatusControl_ServiceModeConfigurationFailed :
                LocaleResolver.HostsStatusControl_ServiceModeRemovalFailed;
            ToastManager.Error(LocaleResolver.Common_Error, message);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to change Hosts service-mode installation to {ShouldInstall}.", shouldInstall);
            ToastManager.Error(LocaleResolver.Common_Error, HandledSystemException.Handle(exception).GetFriendlyMessage().ToString());
        }
    }

    private HostsServiceModeChangeRequest? CreateInstallRequest(HostsServiceModeStatus status)
    {
        if (status.State is HostsServiceModeConfigurationState.CurrentExecutable) return null;
        if (status.State is HostsServiceModeConfigurationState.Unavailable)
        {
            ToastManager.Error(LocaleResolver.Common_Error, LocaleResolver.HostsStatusControl_ServiceModeConfigurationFailed);
            return null;
        }

        var assessment = serviceModeManager.AssessEnvironment();
        // if (!await ConfirmInstallationAsync(status, assessment)) return null;
        return new HostsServiceModeChangeRequest(
            true,
            status.State is HostsServiceModeConfigurationState.OtherExecutable or HostsServiceModeConfigurationState.Invalid,
            assessment.RequiresPortableAuthorization);
    }

    private static HostsServiceModeChangeRequest? CreateUninstallRequest(HostsServiceModeStatus status) =>
        status.State switch
        {
            HostsServiceModeConfigurationState.NotConfigured => null,
            HostsServiceModeConfigurationState.CurrentExecutable => new HostsServiceModeChangeRequest(false),
            _ => throw new HostsServiceModeChangeException("The service-mode integration no longer belongs to this executable.")
        };

    private sealed class StateToolTipConverterImpl : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value switch
            {
                HostsServiceModeConfigurationState.NotConfigured => LocaleResolver.HostsStatusControl_ServiceModeNotConfigured,
                HostsServiceModeConfigurationState.CurrentExecutable => LocaleResolver.HostsStatusControl_ServiceModeCurrentExecutable,
                HostsServiceModeConfigurationState.OtherExecutable => LocaleResolver.HostsStatusControl_ServiceModeOtherExecutable,
                HostsServiceModeConfigurationState.Invalid => LocaleResolver.HostsStatusControl_ServiceModeInvalid,
                HostsServiceModeConfigurationState.Unavailable => LocaleResolver.HostsStatusControl_ServiceModeUnavailableStatus,
                _ => null
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}