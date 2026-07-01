using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;
using Color = Avalonia.Media.Color;

namespace Everywhere.Common;

public enum ColoredIconType
{
    Lucide,
    Text,
}

/// <summary>
/// Represents an icon.
/// </summary>
public partial class ColoredIcon(ColoredIconType type, SerializableColor? foreground = null, SerializableColor? background = null) : ObservableObject
{
    [JsonIgnore]
    public Color? ForegroundColor
    {
        get => Foreground;
        set => Foreground = value;
    }

    [JsonIgnore]
    public Color? BackgroundColor
    {
        get => Background;
        set => Background = value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForegroundColor))]
    public partial SerializableColor? Foreground { get; set; } = foreground;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundColor))]
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