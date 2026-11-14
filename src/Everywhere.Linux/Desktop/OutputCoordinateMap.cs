using Avalonia;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Maps one UI output's pixel coordinates to compositor logical coordinates.
/// </summary>
public readonly record struct OutputCoordinateMap(PixelRect AvaloniaBounds, PixelRect LogicalBounds)
{
    /// <summary>
    /// Maps an Avalonia output point to global compositor logical coordinates.
    /// </summary>
    public PixelPoint ToLogical(PixelPoint point)
    {
        return new(
            LogicalBounds.X + (int)Math.Round((point.X - AvaloniaBounds.X) * (double)LogicalBounds.Width / AvaloniaBounds.Width),
            LogicalBounds.Y + (int)Math.Round((point.Y - AvaloniaBounds.Y) * (double)LogicalBounds.Height / AvaloniaBounds.Height));
    }

    /// <summary>
    /// Maps compositor logical bounds to outward-rounded Avalonia output bounds.
    /// </summary>
    public PixelRect ToAvalonia(PixelRect bounds)
    {
        var left = AvaloniaBounds.X + (bounds.X - LogicalBounds.X) * (double)AvaloniaBounds.Width / LogicalBounds.Width;
        var top = AvaloniaBounds.Y + (bounds.Y - LogicalBounds.Y) * (double)AvaloniaBounds.Height / LogicalBounds.Height;
        var right = AvaloniaBounds.X + (bounds.Right - LogicalBounds.X) * (double)AvaloniaBounds.Width / LogicalBounds.Width;
        var bottom = AvaloniaBounds.Y + (bounds.Bottom - LogicalBounds.Y) * (double)AvaloniaBounds.Height / LogicalBounds.Height;
        return new PixelRect(
            (int)Math.Floor(left),
            (int)Math.Floor(top),
            (int)Math.Ceiling(right) - (int)Math.Floor(left),
            (int)Math.Ceiling(bottom) - (int)Math.Floor(top));
    }
}