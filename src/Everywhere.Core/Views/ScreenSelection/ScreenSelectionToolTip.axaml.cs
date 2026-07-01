using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Everywhere.Interop;

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

    public IVisualElement? Element
    {
        set => Header = GetElementDescription(value);
    }

    private readonly Dictionary<int, string> _processNameCache = new();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModeProperty)
        {
            RaisePropertyChanged(TipTextProperty, string.Empty, TipText);
        }
    }

    private string? GetElementDescription(IVisualElement? element)
    {
        if (element is null) return LocaleResolver.Common_None;

        DynamicLocaleKey key;
        var elementTypeKey = new DynamicLocaleKey($"VisualElementType_{element.Type}");
        if (element.ProcessId > 0)
        {
            if (!_processNameCache.TryGetValue(element.ProcessId, out var processName))
            {
                try
                {
                    using var process = Process.GetProcessById(element.ProcessId);
                    processName = process.ProcessName;
                }
                catch
                {
                    processName = string.Empty;
                }
                _processNameCache[element.ProcessId] = processName;
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