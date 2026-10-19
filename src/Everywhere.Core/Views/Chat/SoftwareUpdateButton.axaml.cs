using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Threading;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
///     Presents the shared update operation in the chat toolbar without owning its state or lifetime.
/// </summary>
[PseudoClasses(":available")]
public sealed class SoftwareUpdateButton : Button
{
    /// <summary>
    /// Defines the application-owned update coordinator.
    /// </summary>
    public static readonly StyledProperty<ISoftwareUpdater?> SoftwareUpdaterProperty =
        AvaloniaProperty.Register<SoftwareUpdateButton, ISoftwareUpdater?>(nameof(SoftwareUpdater));

    /// <summary>
    /// Defines the live-localized tooltip and accessible name.
    /// </summary>
    public static readonly DirectProperty<SoftwareUpdateButton, IDynamicLocaleKey?> ToolTipKeyProperty =
        AvaloniaProperty.RegisterDirect<SoftwareUpdateButton, IDynamicLocaleKey?>(nameof(ToolTipKey), o => o.ToolTipKey);

    /// <summary>
    /// Gets or sets the coordinator; this control never owns its lifetime.
    /// </summary>
    public ISoftwareUpdater? SoftwareUpdater
    {
        get => GetValue(SoftwareUpdaterProperty);
        set => SetValue(SoftwareUpdaterProperty, value);
    }

    /// <summary>
    /// Gets the tooltip key, resolved by the template rather than cached as text.
    /// </summary>
    public IDynamicLocaleKey ToolTipKey => _toolTipKey ?? DirectLocaleKey.Empty;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(SoftwareUpdateButton);

    private ISoftwareUpdater? _subscribedUpdater;
    private bool _isAttached;
    private IDynamicLocaleKey? _toolTipKey;

    static SoftwareUpdateButton()
    {
        IsVisibleProperty.OverrideDefaultValue<SoftwareUpdateButton>(false);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Subscribe(SoftwareUpdater);
        UpdateState();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Subscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != SoftwareUpdaterProperty) return;

        if (_isAttached) Subscribe(SoftwareUpdater);
        UpdateState();
    }

    private void Subscribe(ISoftwareUpdater? updater)
    {
        if (ReferenceEquals(_subscribedUpdater, updater)) return;

        if (_subscribedUpdater is not null)
        {
            _subscribedUpdater.PropertyChanged -= HandleSoftwareUpdaterPropertyChanged;
        }

        _subscribedUpdater = updater;
        if (_subscribedUpdater is not null)
        {
            _subscribedUpdater.PropertyChanged += HandleSoftwareUpdaterPropertyChanged;
        }
    }

    private void HandleSoftwareUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(ISoftwareUpdater.CurrentOperation) or
            nameof(ISoftwareUpdater.LatestUpdate) or
            nameof(ISoftwareUpdater.ReadyUpdate) or
            nameof(ISoftwareUpdater.DisplayUpdate) or
            nameof(ISoftwareUpdater.IsUpdateActionVisible))
        {
            Dispatcher.UIThread.PostOnDemand(UpdateState, DispatcherPriority.Normal);
        }
    }

    private void UpdateState()
    {
        var updater = SoftwareUpdater;
        var operation = updater?.CurrentOperation;
        var display = updater?.DisplayUpdate;
        var isReady = display is not null && operation is not { IsActive: true } && updater?.ReadyUpdate?.HasSamePackageIdentity(display) == true;
        SetCurrentValue(IsVisibleProperty, updater?.IsUpdateActionVisible == true);
        SetCurrentValue(IsEnabledProperty, operation is not { IsActive: true } || operation.CanCancel);
        PseudoClasses.Set(":available", display is not null && operation is not { IsActive: true });

        var tooltip = operation switch
        {
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Checking } => new DynamicLocaleKey(LocaleKey.SoftwareUpdateButton_Checking),
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Downloading } => CreateDownloadingTooltip(operation),
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Validating } => new DynamicLocaleKey(LocaleKey.SoftwareUpdateButton_Validating),
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Cancelling } => new DynamicLocaleKey(LocaleKey.SoftwareUpdateButton_Cancelling),
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Installing } => new DynamicLocaleKey(LocaleKey.SoftwareUpdateButton_Installing),
            { Stage: SoftwareUpdateOperationStage.Failed } => new DynamicLocaleKey(LocaleKey.SoftwareUpdateButton_Failed),
            { Stage: SoftwareUpdateOperationStage.Canceled } => new DynamicLocaleKey(LocaleKey.SoftwareUpdateButton_Canceled),
            _ when isReady && display is not null => new DynamicLocaleKey(LocaleKey.SoftwareUpdateButton_Ready),
            _ when display is not null =>
                new FormattedDynamicLocaleKey(
                    LocaleKey.SoftwareUpdateButton_ToolTip,
                    RuntimeConstants.Version.ToString(),
                    display.Version.ToString(),
                    display.PublishedAt.DateTime.ToLocalTime()),
            _ => null
        };

        SetAndRaise(ToolTipKeyProperty, ref _toolTipKey, tooltip);
    }

    private static FormattedDynamicLocaleKey CreateDownloadingTooltip(SoftwareUpdateOperation operation)
    {
        var version = operation.Target?.Version.ToString() ?? string.Empty;
        var percent = double.IsNaN(operation.Progress) ? "0" : (operation.Progress * 100d).ToString("0", CultureInfo.CurrentCulture);
        return new FormattedDynamicLocaleKey(LocaleKey.SoftwareUpdateButton_Downloading, version, $"{percent}%");
    }

    /// <inheritdoc />
    protected override void OnClick()
    {
        if (SoftwareUpdater is not { } updater) return;

        if (updater.CurrentOperation is { IsActive: true, CanCancel: true })
        {
            updater.CancelCurrentOperation();
            return;
        }

        if (updater.CurrentOperation is { IsActive: true }) return;

        InstallAsync(updater).Detach();
    }

    private static async Task InstallAsync(ISoftwareUpdater updater)
    {
        try
        {
            await updater.PerformUpdateAsync();
        }
        catch (Exception ex)
        {
            ToastManager.Error(LocaleKey.Common_Error.I18N(), ex.GetFriendlyMessage());
        }
    }
}