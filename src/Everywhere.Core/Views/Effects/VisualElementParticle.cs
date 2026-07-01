using Avalonia.Controls;

namespace Everywhere.Views;

public abstract class VisualElementParticle : UserControl
{
    public abstract void Spawn(
        Point startPosition,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize);

    public abstract void Recycle();

    /// <summary>
    /// return true means dead and should be recycled, false means still alive and should remain active
    /// </summary>
    /// <param name="deltaTimeMs"></param>
    /// <returns></returns>
    public abstract bool Update(double deltaTimeMs);
}