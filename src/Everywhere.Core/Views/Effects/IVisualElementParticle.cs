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

    /// <summary>
    /// Gets whether the particle advances on the compositor and signals completion via
    /// <see cref="RegisterCompletionCallback"/> instead of relying on per-frame UI updates.
    /// </summary>
    bool IsSelfAnimating => false;

    /// <summary>
    /// Registers the host callback invoked on the UI thread when a self-animating particle finishes.
    /// </summary>
    /// <param name="callback"></param>
    void RegisterCompletionCallback(Action? callback) { }
}