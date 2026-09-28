namespace Everywhere.Views;

/// <summary>
/// Defines the pooled lifecycle of a visual-element effect particle.
/// </summary>
public interface IVisualElementParticle
{
    /// <summary>
    /// Initializes a pooled particle for one animation.
    /// </summary>
    /// <param name="owner"></param>
    /// <param name="startPosition"></param>
    /// <param name="targetTracker"></param>
    /// <param name="startContent"></param>
    /// <param name="endContent"></param>
    /// <param name="startSize"></param>
    void Spawn(
        VisualElementEffectWindow owner,
        Point startPosition,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize);

    /// <summary>
    /// Releases per-animation references before the particle returns to its pool.
    /// </summary>
    void Recycle();

    /// <summary>
    /// Advances the particle and returns whether it is ready to be recycled.
    /// </summary>
    /// <param name="deltaTimeMs"></param>
    /// <returns></returns>
    bool Update(double deltaTimeMs);
}