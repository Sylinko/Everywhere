using Avalonia.Controls;
using Avalonia.Platform;

namespace Everywhere.Views;

public sealed class VisualElementEffectWindow : VisualElementOverlayWindow
{
    public double Scale { get; private set; }

    private readonly VisualElementParticleHost<PickVisualElementParticle> _pickHost;
    private readonly VisualElementParticleHost<ScanVisualElementParticle> _scanHost;

    private PixelRect _screenBounds;

    public VisualElementEffectWindow()
    {
        var panel = new Panel();
        _scanHost = new VisualElementParticleHost<ScanVisualElementParticle>(this, 5);
        _pickHost = new VisualElementParticleHost<PickVisualElementParticle>(this, 2);
        panel.Children.Add(_scanHost);
        panel.Children.Add(_pickHost);
        Content = panel;
    }

    public void AddParticle<T>(
        Point startPoint,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize) where T : VisualElementParticle
    {
        if (typeof(T) == typeof(ScanVisualElementParticle)) _scanHost.SpawnParticle(startPoint, targetTracker, startContent, endContent, startSize);
        else _pickHost.SpawnParticle(startPoint, targetTracker, startContent, endContent, startSize);
    }

    public void SetPlacement(Screen targetScreen)
    {
        _screenBounds = targetScreen.Bounds;
        Position = _screenBounds.Position;
        Scale = DesktopScaling; // we must set Position first to get the correct scaling factor
        Width = _screenBounds.Width / Scale;
        Height = _screenBounds.Height / Scale - 1d; // Let the window slightly smaller than screen, avoid focus assist
    }

    public Point ScreenPixelToLocal(PixelPoint screenPoint)
    {
        return new Point(
            (screenPoint.X - _screenBounds.X) / Scale,
            (screenPoint.Y - _screenBounds.Y) / Scale);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = e is { IsProgrammatic: false, CloseReason: not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown };

        base.OnClosing(e);
    }

    public void HandleHostIdle()
    {
        if (!_pickHost.HasActiveParticles && !_scanHost.HasActiveParticles) Hide();
    }
}