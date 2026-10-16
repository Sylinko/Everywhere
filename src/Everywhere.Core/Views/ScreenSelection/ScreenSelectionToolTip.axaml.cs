using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Everywhere.Automation;
using Everywhere.Interop;

namespace Everywhere.Views;

public class ScreenSelectionToolTip(INativeHelper nativeHelper, IEnumerable<ScreenSelectionMode> allowedModes) : TemplatedControl
{
    public static readonly StyledProperty<IDynamicLocaleKey> HeaderProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, IDynamicLocaleKey>(nameof(Header), DirectLocaleKey.Empty);

    public IDynamicLocaleKey Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public IEnumerable<ScreenSelectionMode> AllowedModes { get; } = allowedModes;

    public static readonly StyledProperty<bool> IsApplicationWindowPickerProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, bool>(nameof(IsApplicationWindowPicker));

    /// <summary>
    /// Shows the application's friendly name and window-picking instructions.
    /// Set before the session receives its first observation.
    /// </summary>
    public bool IsApplicationWindowPicker
    {
        get => GetValue(IsApplicationWindowPickerProperty);
        set => SetValue(IsApplicationWindowPickerProperty, value);
    }

    public static readonly StyledProperty<ScreenSelectionMode> ModeProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, ScreenSelectionMode>(nameof(Mode));

    public ScreenSelectionMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly DirectProperty<ScreenSelectionToolTip, IDynamicLocaleKey> TipTextProperty =
        AvaloniaProperty.RegisterDirect<ScreenSelectionToolTip, IDynamicLocaleKey>(
            nameof(TipText),
            o => o.TipText);

    public IDynamicLocaleKey TipText => GetTipText(Mode, IsApplicationWindowPicker);

    public static readonly StyledProperty<string?> SizeInfoProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, string?>(nameof(SizeInfo));

    public string? SizeInfo
    {
        get => GetValue(SizeInfoProperty);
        set => SetValue(SizeInfoProperty, value);
    }

    private readonly Dictionary<int, string> _applicationNameCache = new();
    private readonly DynamicLocaleKey _freeTipText = new(LocaleKey.ScreenSelectionToolTip_TipText_Free);
    private readonly DynamicLocaleKey _normalTipText = new(LocaleKey.ScreenSelectionToolTip_TipText_Normal);
    private readonly DynamicLocaleKey _applicationWindowTipText = new(LocaleKey.ScreenSelectionToolTip_TipText_ApplicationWindow);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModeProperty || change.Property == IsApplicationWindowPickerProperty)
        {
            var oldMode = change.OldValue is ScreenSelectionMode mode ? mode : Mode;
            var wasApplicationWindowPicker =
                change.OldValue is bool isApplicationWindowPicker ? isApplicationWindowPicker : IsApplicationWindowPicker;
            var oldTipText = GetTipText(oldMode, wasApplicationWindowPicker);

            if (!ReferenceEquals(oldTipText, TipText))
            {
                RaisePropertyChanged(TipTextProperty, oldTipText, TipText);
            }
        }
    }

    /// <summary>Updates the current picker observation and its provider failure as one UI state.</summary>
    public void SetObservation(
        VisualElementSnapshot? snapshot,
        VisualElementQueryFailureKind? failureKind,
        IDynamicLocaleKey? failureMessage)
    {
        if (IsApplicationWindowPicker)
        {
            // A failed or cleared candidate must never leave the previous application's name visible.
            var applicationName = failureKind is null && failureMessage is null ? GetApplicationName(snapshot) : null;
            Header = string.IsNullOrEmpty(applicationName) ? DirectLocaleKey.Empty : new DirectLocaleKey(applicationName);
            return;
        }

        Header = failureMessage ?? GetElementDescription(snapshot, failureKind);
    }

    private DynamicLocaleKey GetElementDescription(VisualElementSnapshot? snapshot, VisualElementQueryFailureKind? failureKind)
    {
        if (failureKind is { } failure)
        {
            var failureKey = failure switch
            {
                VisualElementQueryFailureKind.PermissionDenied => LocaleKey.ScreenSelectionToolTip_QueryFailure_PermissionDenied,
                VisualElementQueryFailureKind.Timeout => LocaleKey.ScreenSelectionToolTip_QueryFailure_Timeout,
                VisualElementQueryFailureKind.ElementUnavailable => LocaleKey.ScreenSelectionToolTip_QueryFailure_ElementUnavailable,
                VisualElementQueryFailureKind.Unsupported => LocaleKey.ScreenSelectionToolTip_QueryFailure_Unsupported,
                _ => LocaleKey.ScreenSelectionToolTip_QueryFailure_ProviderFailure,
            };
            return new DynamicLocaleKey(failureKey);
        }

        if (snapshot is not { } observation) return new DynamicLocaleKey(LocaleKey.Common_None);

        var elementTypeKey = new DynamicLocaleKey($"VisualElementType_{observation.Type ?? VisualElementType.Unknown}");
        var applicationName = GetApplicationName(snapshot);
        return string.IsNullOrWhiteSpace(applicationName) ?
            elementTypeKey :
            new FormattedDynamicLocaleKey(
                LocaleKey.ScreenSelectionToolTip_ElementDescription,
                new DirectLocaleKey(applicationName),
                elementTypeKey);
    }

    private string? GetApplicationName(VisualElementSnapshot? snapshot)
    {
        if (snapshot?.ProcessId is not ({ } processId and > 0))
        {
            return null;
        }

        if (_applicationNameCache.TryGetValue(processId, out var applicationName))
        {
            return applicationName;
        }

        try
        {
            applicationName = nativeHelper.GetApplicationInfo(processId)?.DisplayName;
            if (string.IsNullOrWhiteSpace(applicationName))
            {
                return null;
            }

            _applicationNameCache[processId] = applicationName;
            return applicationName;
        }
        catch (Exception exception) when
            (exception is ArgumentException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private DynamicLocaleKey GetTipText(ScreenSelectionMode mode, bool isApplicationWindowPicker)
    {
        return isApplicationWindowPicker ? _applicationWindowTipText :
            mode is ScreenSelectionMode.Free ?
                _freeTipText : _normalTipText;
    }
}