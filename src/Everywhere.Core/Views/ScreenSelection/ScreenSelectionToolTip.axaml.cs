using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Everywhere.Interop;

using Everywhere.Automation;

namespace Everywhere.Views;

public class ScreenSelectionToolTip(IEnumerable<ScreenSelectionMode> allowedModes) : TemplatedControl
{
    public static readonly StyledProperty<IDynamicLocaleKey> HeaderProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, IDynamicLocaleKey>(nameof(Header), DirectLocaleKey.Empty);

    public IDynamicLocaleKey Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public IEnumerable<ScreenSelectionMode> AllowedModes { get; } = allowedModes;

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

    public IDynamicLocaleKey TipText => GetTipText(Mode);

    public static readonly StyledProperty<string?> SizeInfoProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, string?>(nameof(SizeInfo));

    public string? SizeInfo
    {
        get => GetValue(SizeInfoProperty);
        set => SetValue(SizeInfoProperty, value);
    }

    /// <summary>Gets or sets the current scalar picker observation.</summary>
    public VisualElementSnapshot? Snapshot
    {
        get => _snapshot;
        set => SetObservation(value, null);
    }

    private readonly Dictionary<int, string> _processNameCache = new();
    private readonly DynamicLocaleKey _freeTipText = new(LocaleKey.ScreenSelectionToolTip_TipText_Free);
    private readonly DynamicLocaleKey _normalTipText = new(LocaleKey.ScreenSelectionToolTip_TipText_Normal);
    private VisualElementSnapshot? _snapshot;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModeProperty)
        {
            var oldTipText = GetTipText((ScreenSelectionMode)change.OldValue!);
            if (!ReferenceEquals(oldTipText, TipText))
            {
                RaisePropertyChanged(TipTextProperty, oldTipText, TipText);
            }
        }
    }

    /// <summary>Updates the current picker observation and its provider failure as one UI state.</summary>
    public void SetObservation(VisualElementSnapshot? snapshot, VisualElementQueryFailureKind? failureKind)
    {
        _snapshot = snapshot;
        Header = GetElementDescription(snapshot, failureKind);
    }

    private DynamicLocaleKey GetElementDescription(VisualElementSnapshot? snapshot, VisualElementQueryFailureKind? failureKind)
    {
        if (snapshot is null && failureKind is { } failure)
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

        DynamicLocaleKey key;
        var elementTypeKey = new DynamicLocaleKey($"VisualElementType_{observation.Type ?? VisualElementType.Unknown}");
        var processId = observation.ProcessId.GetValueOrDefault(-1);
        if (processId > 0)
        {
            if (!_processNameCache.TryGetValue(processId, out var processName))
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    processName = process.ProcessName;
                }
                catch
                {
                    processName = string.Empty;
                }
                _processNameCache[processId] = processName;
            }

            key = processName.IsNullOrWhiteSpace() ?
                elementTypeKey :
                new FormattedDynamicLocaleKey(
                    LocaleKey.ScreenSelectionToolTip_ElementDescription,
                    new DirectLocaleKey(processName),
                    elementTypeKey);
        }
        else
        {
            key = elementTypeKey;
        }

        return key;
    }

    private DynamicLocaleKey GetTipText(ScreenSelectionMode mode) =>
        mode is ScreenSelectionMode.Free ? _freeTipText : _normalTipText;
}