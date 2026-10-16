using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;

namespace Everywhere.Common;

public enum ColoredIconType
{
    Lucide,
    Text,
}

/// <summary>
/// Represents an icon.
/// </summary>
/// TODO: make this a messagepack object with polymorphism support for Lucide and Text types.
public partial class ColoredIcon(ColoredIconType type, SerializableColor? foreground = null, SerializableColor? background = null) : ObservableObject
{
    [ObservableProperty]
    public partial SerializableColor? Foreground { get; set; } = foreground;

    [ObservableProperty]
    public partial SerializableColor? Background { get; set; } = background;

    [ObservableProperty]
    public partial ColoredIconType Type { get; set; } = type;

    [ObservableProperty]
    public partial LucideIconKind Kind { get; set; }

    public string? Text
    {
        get;
        set => SetProperty(ref field, value?.SafeSubstring(0, 10));
    }

    public static implicit operator ColoredIcon(LucideIconKind kind) => new(ColoredIconType.Lucide) { Kind = kind };

    public static implicit operator ColoredIcon(string text) => new(ColoredIconType.Text) { Text = text };
}