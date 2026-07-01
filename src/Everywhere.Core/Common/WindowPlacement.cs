using System.Text.Json.Serialization;
using Avalonia.Controls;
using MessagePack;

namespace Everywhere.Common;

/// <summary>
/// Represents the placement of a window (position, size and state).
/// </summary>
[Serializable]
[MessagePackObject]
public partial struct WindowPlacement(PixelPoint position, int width, int height, WindowState windowState) : IEquatable<WindowPlacement>
{
    [Key(0)]
    public int X { get; set; } = position.X;

    [Key(1)]
    public int Y { get; set; } = position.Y;

    [Key(2)]
    public int Width { get; set; } = width;

    [Key(3)]
    public int Height { get; set; } = height;

    [Key(4)]
    public WindowState WindowState { get; set; } = windowState;

    [JsonIgnore]
    [IgnoreMember]
    public PixelPoint Position => new(X, Y);

    public bool Equals(WindowPlacement other) =>
        X == other.X && Y == other.Y && Width == other.Width && Height == other.Height && WindowState == other.WindowState;

    public override bool Equals(object? obj) => obj is WindowPlacement other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height, (int)WindowState);

    public static bool operator ==(WindowPlacement left, WindowPlacement right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(WindowPlacement left, WindowPlacement right)
    {
        return !(left == right);
    }
}