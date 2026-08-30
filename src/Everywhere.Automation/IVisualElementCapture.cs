using Avalonia;
using Avalonia.Platform;

namespace Everywhere.Automation;

/// <summary>
/// Exposes an owned bitmap buffer captured from a visual element.
/// </summary>
/// <remarks>
/// The contract describes physical pixels only and deliberately carries no logical DPI. Presentation adapters choose their own density metadata; the Avalonia adapter currently normalizes it to 96 DPI.
/// </remarks>
public interface IVisualElementCapture : IDisposable
{
    /// <summary>Gets the captured pixel format.</summary>
    PixelFormat Format { get; }

    /// <summary>Gets the captured alpha format.</summary>
    AlphaFormat AlphaFormat { get; }

    /// <summary>Gets the address of the captured pixel buffer.</summary>
    nint Data { get; }

    /// <summary>Gets the actual pixel dimensions of the captured buffer.</summary>
    PixelSize Size { get; }

    /// <summary>Gets the number of bytes between adjacent bitmap rows.</summary>
    int Stride { get; }
}