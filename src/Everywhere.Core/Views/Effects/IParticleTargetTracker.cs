namespace Everywhere.Views;

public interface IParticleTargetTracker
{
    bool IsCancelled { get; }

    bool TryGetTargetCenterOnScreen(out PixelPoint point);
    
    void OnParticleCompleted();
}