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

            var request = shouldInstall ? await CreateInstallRequestAsync(status) : CreateUninstallRequest(status);
            if (request is null) return;

            await coordinator.ChangeServiceModeAsync(request);
        }
        catch (HostsServiceModeChangeException)
        {
            var message = shouldInstall ?
                LocaleKey.HostsStatusControl_ServiceModeConfigurationFailed.I18N() :
                LocaleKey.HostsStatusControl_ServiceModeRemovalFailed.I18N();
            ToastManager.Error(LocaleKey.Common_Error.I18N(), message);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to change Hosts service-mode installation to {ShouldInstall}.", shouldInstall);
            ToastManager.Error(LocaleKey.Common_Error.I18N(), HandledSystemException.Handle(exception).GetFriendlyMessage().ToString());
        }
    }

    private async Task<HostsServiceModeChangeRequest?> CreateInstallRequestAsync(HostsServiceModeStatus status)
    {
        if (status.State is HostsServiceModeConfigurationState.CurrentExecutable) return null;
        if (status.State is HostsServiceModeConfigurationState.Unavailable)
        {
            ToastManager.Error(LocaleKey.Common_Error.I18N(), LocaleKey.HostsStatusControl_ServiceModeConfigurationFailed.I18N());
            return null;
        }

        var shouldReplaceExisting = status.State is HostsServiceModeConfigurationState.OtherExecutable or HostsServiceModeConfigurationState.Invalid;
        if (shouldReplaceExisting)
        {
            var message = status.State is HostsServiceModeConfigurationState.OtherExecutable ?
                LocaleKey.HostsServiceModeControl_ReplaceConfirmation.I18N().Format(status.ConfiguredExecutablePath) :
                LocaleKey.HostsServiceModeControl_RebuildConfirmation.I18N();
            var result = await DialogManager.CreateDialog(message, LocaleKey.Common_Warning.I18N(), TopLevel.GetTopLevel(this))
                .WithPrimaryButton(LocaleKey.Common_Yes.I18N())
                .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                .ShowAsync();
            if (result != DialogResult.Primary) return null;
        }

        return new HostsServiceModeChangeRequest(true, shouldReplaceExisting);
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
                HostsServiceModeConfigurationState.NotConfigured => LocaleKey.HostsStatusControl_ServiceModeNotConfigured.I18N(),
                HostsServiceModeConfigurationState.CurrentExecutable => LocaleKey.HostsStatusControl_ServiceModeCurrentExecutable.I18N(),
                HostsServiceModeConfigurationState.OtherExecutable => LocaleKey.HostsStatusControl_ServiceModeOtherExecutable.I18N(),
                HostsServiceModeConfigurationState.Invalid => LocaleKey.HostsStatusControl_ServiceModeInvalid.I18N(),
                HostsServiceModeConfigurationState.Unavailable => LocaleKey.HostsStatusControl_ServiceModeUnavailableStatus.I18N(),
                _ => null
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}