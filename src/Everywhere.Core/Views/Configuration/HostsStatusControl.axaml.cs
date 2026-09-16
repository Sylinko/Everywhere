using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

/// <summary>Presentation state for one isolated Host row.</summary>
/// <param name="State">Current connection state.</param>
/// <param name="StatusText">Localized status text.</param>
/// <param name="ToolTip">Optional localized tooltip.</param>
public sealed record HostStatusPresentation(HostConnectionState State, IDynamicLocaleKey StatusText, IDynamicLocaleKey ToolTip);

/// <summary>Displays independent Input and Automation Host connection health.</summary>
public partial class HostsStatusControl(HostProcessCoordinator coordinator, ILogger<HostsStatusControl> logger) : TemplatedControl
{
    public static readonly DirectProperty<HostsStatusControl, HostStatusPresentation> InputStatusProperty =
        AvaloniaProperty.RegisterDirect<HostsStatusControl, HostStatusPresentation>(nameof(InputStatus), control => control.InputStatus);

    public static readonly DirectProperty<HostsStatusControl, HostStatusPresentation> AutomationStatusProperty =
        AvaloniaProperty.RegisterDirect<HostsStatusControl, HostStatusPresentation>(nameof(AutomationStatus), control => control.AutomationStatus);

    public HostStatusPresentation InputStatus
    {
        get;
        private set => SetAndRaise(InputStatusProperty, ref field, value);
    } = CreatePresentation(coordinator.GetStatus(ProcessRole.Input));

    public HostStatusPresentation AutomationStatus
    {
        get;
        private set => SetAndRaise(AutomationStatusProperty, ref field, value);
    } = CreatePresentation(coordinator.GetStatus(ProcessRole.Automation));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        coordinator.StatusChanged += HandleStatusChanged;
        ApplyStatus(coordinator.GetStatus(ProcessRole.Input));
        ApplyStatus(coordinator.GetStatus(ProcessRole.Automation));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        coordinator.StatusChanged -= HandleStatusChanged;
        base.OnDetachedFromVisualTree(e);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RetryHostsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await coordinator.RestartHostsAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to restart the isolated Hosts.");
            exception = HandledSystemException.Handle(exception);
            ToastManager.Error(LocaleResolver.Common_Error, exception.GetFriendlyMessage().ToString());
        }
    }

    private void HandleStatusChanged(HostRoleStatus status) =>
        Dispatcher.UIThread.PostOnDemand(() => ApplyStatus(status));

    private void ApplyStatus(HostRoleStatus status)
    {
        var presentation = CreatePresentation(status);
        switch (status.Role)
        {
            case ProcessRole.Input:
                InputStatus = presentation;
                break;
            case ProcessRole.Automation:
                AutomationStatus = presentation;
                break;
        }
    }

    private static HostStatusPresentation CreatePresentation(HostRoleStatus status)
    {
        var statusTextKey = status.State switch
        {
            HostConnectionState.Starting => LocaleKey.HostsStatusControl_Connecting,
            HostConnectionState.Connected => LocaleKey.HostsStatusControl_Connected,
            HostConnectionState.Degraded => LocaleKey.HostsStatusControl_ConnectedFallback,
            HostConnectionState.Unavailable => LocaleKey.HostsStatusControl_Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, null)
        };
        IDynamicLocaleKey explanation = status.State switch
        {
            HostConnectionState.Degraded => new DynamicLocaleKey(LocaleKey.HostsStatusControl_Fallback_ToolTip),
            HostConnectionState.Unavailable => new DynamicLocaleKey(LocaleKey.HostsStatusControl_Unavailable_ToolTip),
            _ => DirectLocaleKey.Empty
        };
        var toolTip = status.MessageKey ?? explanation;
        return new HostStatusPresentation(status.State, new DynamicLocaleKey(statusTextKey), toolTip);
    }
}