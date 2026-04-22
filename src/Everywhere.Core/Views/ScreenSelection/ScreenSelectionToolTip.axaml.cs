using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Everywhere.Interop;

namespace Everywhere.Views;

public class ScreenSelectionToolTip(ScreenSelectionModes allowedModes) : TemplatedControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, string?>(nameof(Header));

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public IEnumerable<ScreenSelectionModes> AllowedModes { get; } = GetAllowedModes(allowedModes);

    public static readonly StyledProperty<ScreenSelectionModes> CurrentModeProperty =
        AvaloniaProperty.Register<ScreenSelectionToolTip, ScreenSelectionModes>(nameof(CurrentMode));

    public ScreenSelectionModes CurrentMode
    {
        get => GetValue(CurrentModeProperty);
        set => SetValue(CurrentModeProperty, value);
    }

    public static readonly DirectProperty<ScreenSelectionToolTip, string> TipTextProperty =
        AvaloniaProperty.RegisterDirect<ScreenSelectionToolTip, string>(
        nameof(TipText),
        o => o.TipText);

    public string TipText => CurrentMode == ScreenSelectionModes.Free ?
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

        if (change.Property == CurrentModeProperty)
        {
            RaisePropertyChanged(TipTextProperty, string.Empty, TipText);
        }
    }

    private string? GetElementDescription(IVisualElement? element)
    {
        if (element is null) return LocaleResolver.Common_None;

        DynamicResourceKey key;
        var elementTypeKey = new DynamicResourceKey($"VisualElementType_{element.Type}");
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
                new FormattedDynamicResourceKey("{0} - {1}", new DirectResourceKey(processName), elementTypeKey);
        }
        else
        {
            key = elementTypeKey;
        }

        return key.ToString();
    }

    private static List<ScreenSelectionModes> GetAllowedModes(ScreenSelectionModes allowedModes)
    {
        var results = new List<ScreenSelectionModes>();
        if (allowedModes.HasFlag(ScreenSelectionModes.Screen)) results.Add(ScreenSelectionModes.Screen);
        if (allowedModes.HasFlag(ScreenSelectionModes.Window)) results.Add(ScreenSelectionModes.Window);
        if (allowedModes.HasFlag(ScreenSelectionModes.Element)) results.Add(ScreenSelectionModes.Element);
        if (allowedModes.HasFlag(ScreenSelectionModes.Free)) results.Add(ScreenSelectionModes.Free);
        return results;
    }
}