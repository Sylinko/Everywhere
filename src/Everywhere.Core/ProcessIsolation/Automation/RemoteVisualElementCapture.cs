using Avalonia.Platform;
using Everywhere.Automation;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Owns a pinned Main-process copy of one raw bitmap streamed by the Automation Host.</summary>
public sealed class RemoteVisualElementCapture : IVisualElementCapture
{
    /// <inheritdoc />
    public PixelRect Bounds { get; }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <inheritdoc />
    public AlphaFormat AlphaFormat { get; }

    /// <inheritdoc />
    public nint Data => _dataHandle.AddrOfPinnedObject();

    /// <inheritdoc />
    public PixelSize Size { get; }

    /// <inheritdoc />
    public int Stride { get; }

    private GCHandle _dataHandle;
    private bool _isDisposed;

    internal RemoteVisualElementCapture(AutomationCaptureHeader header, byte[] data)
    {
        Bounds = new PixelRect(header.BoundsX, header.BoundsY, header.BoundsWidth, header.BoundsHeight);
        Format = header.PixelFormat.ToPixelFormat();
        AlphaFormat = header.AlphaFormat.ToAlphaFormat();
        Size = new PixelSize(header.PixelWidth, header.PixelHeight);
        Stride = header.Stride;
        _dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _dataHandle.Free();
        GC.SuppressFinalize(this);
    }

    ~RemoteVisualElementCapture()
    {
        if (_dataHandle.IsAllocated) _dataHandle.Free();
    }
}