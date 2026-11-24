using CommunityToolkit.Mvvm.ComponentModel;
using Tabler.Avalonia;

namespace Everywhere.Common;

public enum ColoredIconType
{
    Tabler,
    Text,
}

/// <summary>
/// Represents an icon.
/// </summary>
/// TODO: make this a messagepack object with polymorphism support for Tabler and Text types.
public partial class ColoredIcon(ColoredIconType type, SerializableColor? foreground = null, SerializableColor? background = null) : ObservableObject
{
    [ObservableProperty]
    public partial SerializableColor? Foreground { get; set; } = foreground;

    [ObservableProperty]
    public partial SerializableColor? Background { get; set; } = background;

    [ObservableProperty]
    public partial ColoredIconType Type { get; set; } = type;

    [ObservableProperty]
    public partial TablerIconKind Kind { get; set; }

    [ObservableProperty]
    public partial bool IsFilled { get; set; }

    public string? Text
    {
        get;
        set => SetProperty(ref field, value?.SafeSubstring(0, 10));
    }

    public static implicit operator ColoredIcon(TablerIconKind kind) => new(ColoredIconType.Tabler) { Kind = kind };

    public static implicit operator ColoredIcon(string text) => new(ColoredIconType.Text) { Text = text };
}