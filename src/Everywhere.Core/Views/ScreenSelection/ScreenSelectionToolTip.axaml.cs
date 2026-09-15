using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Everywhere.Interop;

using Everywhere.Automation;

namespace Everywhere.Views;

public class ScreenSelectionToolTip(IEnumerable<ScreenSelectionMode> allowedModes) : TemplatedControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, string?>(nameof(Header));

    public string? Header
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

    public static readonly DirectProperty<ScreenSelectionToolTip, string> TipTextProperty =
        AvaloniaProperty.RegisterDirect<ScreenSelectionToolTip, string>(
        nameof(TipText),
        o => o.TipText);

    public string TipText => Mode == ScreenSelectionMode.Free ?
        LocaleResolver.ScreenSelectionToolTip_TipText_Free :
        LocaleResolver.ScreenSelectionToolTip_TipText_Normal;

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
    private VisualElementSnapshot? _snapshot;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModeProperty)
        {
            RaisePropertyChanged(TipTextProperty, string.Empty, TipText);
        }
    }

    /// <summary>Updates the current picker observation and its provider failure as one UI state.</summary>
    public void SetObservation(VisualElementSnapshot? snapshot, VisualElementQueryFailureKind? failureKind)
    {
        _snapshot = snapshot;
        Header = GetElementDescription(snapshot, failureKind);
    }

    private string? GetElementDescription(VisualElementSnapshot? snapshot, VisualElementQueryFailureKind? failureKind)
    {
        if (snapshot is null && failureKind is { } failure)
        {
            return failure switch
            {
                VisualElementQueryFailureKind.PermissionDenied => LocaleResolver.ScreenSelectionToolTip_QueryFailure_PermissionDenied,
                VisualElementQueryFailureKind.Timeout => LocaleResolver.ScreenSelectionToolTip_QueryFailure_Timeout,
                VisualElementQueryFailureKind.ElementUnavailable => LocaleResolver.ScreenSelectionToolTip_QueryFailure_ElementUnavailable,
                VisualElementQueryFailureKind.Unsupported => LocaleResolver.ScreenSelectionToolTip_QueryFailure_Unsupported,
                _ => LocaleResolver.ScreenSelectionToolTip_QueryFailure_ProviderFailure,
            };
        }

        if (snapshot is not { } observation) return LocaleResolver.Common_None;

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
                new FormattedDynamicLocaleKey("{0} - {1}", new DirectLocaleKey(processName), elementTypeKey);
        }
        else
        {
            key = elementTypeKey;
        }

        return key.ToString();
    }
}
