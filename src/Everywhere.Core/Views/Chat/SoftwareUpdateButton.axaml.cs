using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Threading;
using Everywhere.Common;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
///     Presents the compact software-update action in the chat toolbar and owns its visual state.
/// </summary>
[PseudoClasses(":available", ":failed")]
public sealed class SoftwareUpdateButton : Button
{
    /// <summary>
    /// Defines the application-owned update coordinator.
    /// </summary>
    public static readonly StyledProperty<ISoftwareUpdater?> SoftwareUpdaterProperty =
        AvaloniaProperty.Register<SoftwareUpdateButton, ISoftwareUpdater?>(nameof(SoftwareUpdater));

    /// <summary>
    /// Defines whether the operation progress ring is visible.
    /// </summary>
    public static readonly DirectProperty<SoftwareUpdateButton, bool> IsBusyProperty =
        AvaloniaProperty.RegisterDirect<SoftwareUpdateButton, bool>(nameof(IsBusy), o => o.IsBusy);

    /// <summary>
    /// Defines whether progress is currently unknown.
    /// </summary>
    public static readonly DirectProperty<SoftwareUpdateButton, bool> IsIndeterminateProperty =
        AvaloniaProperty.RegisterDirect<SoftwareUpdateButton, bool>(nameof(IsIndeterminate), o => o.IsIndeterminate);

    /// <summary>
    /// Defines normalized download progress.
    /// </summary>
    public static readonly DirectProperty<SoftwareUpdateButton, double> ProgressProperty =
        AvaloniaProperty.RegisterDirect<SoftwareUpdateButton, double>(nameof(Progress), o => o.Progress);

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
    /// Gets whether the current operation is active.
    /// </summary>
    public bool IsBusy => _isBusy;

    /// <summary>
    /// Gets whether the progress ring should animate without a known fraction.
    /// </summary>
    public bool IsIndeterminate => _isIndeterminate;

    /// <summary>
    /// Gets the download fraction in the range zero to one.
    /// </summary>
    public double Progress => _progress;

    /// <summary>
    /// Gets the tooltip key, resolved by the template rather than cached as text.
    /// </summary>
    public IDynamicLocaleKey ToolTipKey => _toolTipKey ?? DirectLocaleKey.Empty;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(SoftwareUpdateButton);

    private ISoftwareUpdater? _subscribedUpdater;
    private bool _isAttached;
    private bool _isBusy;
    private bool _isIndeterminate;
    private double _progress;
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
        var isBusy = operation?.IsActive == true;
        var progress = operation?.Progress ?? 0d;

        SetCurrentValue(
            IsVisibleProperty,
            updater?.IsUpdateActionVisible == true);
        SetCurrentValue(
            IsEnabledProperty,
            operation is not
            {
                Stage: SoftwareUpdateOperationStage.Cancelling or SoftwareUpdateOperationStage.Installing
            } && operation is not
            {
                Intent: SoftwareUpdateOperationIntent.Automatic,
                Stage: SoftwareUpdateOperationStage.Validating
            });
        SetAndRaise(
            IsBusyProperty,
            ref _isBusy,
            isBusy);
        SetAndRaise(
            IsIndeterminateProperty,
            ref _isIndeterminate,
            operation?.IsIndeterminate == true || operation?.Stage == SoftwareUpdateOperationStage.Downloading && progress <= 0d);
        SetAndRaise(
            ProgressProperty,
            ref _progress,
            operation?.Stage == SoftwareUpdateOperationStage.Downloading && !double.IsNaN(progress) ? Math.Clamp(progress, 0d, 1d) : 0d);
        PseudoClasses.Set(":available", display is not null && !isBusy);
        PseudoClasses.Set(":failed", operation?.Stage == SoftwareUpdateOperationStage.Failed);

        var tooltip = operation switch
        {
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Checking } => new DynamicLocaleKey(LocaleKey.ChatInputArea_UpdateButton_Checking),
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Downloading } => CreateDownloadingTooltip(operation),
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Validating } => new DynamicLocaleKey(LocaleKey.ChatInputArea_UpdateButton_Validating),
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Cancelling } => new DynamicLocaleKey(LocaleKey.ChatInputArea_UpdateButton_Cancelling),
            { IsActive: true, Stage: SoftwareUpdateOperationStage.Installing } => new DynamicLocaleKey(LocaleKey.ChatInputArea_UpdateButton_Installing),
            { Stage: SoftwareUpdateOperationStage.Failed } => new DynamicLocaleKey(LocaleKey.ChatInputArea_UpdateButton_Failed),
            { Stage: SoftwareUpdateOperationStage.Canceled } => new DynamicLocaleKey(LocaleKey.ChatInputArea_UpdateButton_Canceled),
            _ when isReady && display is not null => new DynamicLocaleKey(LocaleKey.ChatInputArea_UpdateButton_Ready),
            _ when display is not null =>
                new FormattedDynamicLocaleKey(
                    LocaleKey.ChatInputArea_UpdateButton_ToolTip,
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
        return new FormattedDynamicLocaleKey(LocaleKey.ChatInputArea_UpdateButton_Downloading, version, $"{percent}%");
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

        updater.PerformUpdateAsync().Detach(new AnonymousExceptionHandler((exception, _, _, _) =>
            ToastManager.Error(LocaleKey.Common_Error.I18N(), exception.GetFriendlyMessage())));
    }
}