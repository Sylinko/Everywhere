using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// A lightweight host for control-based particles.
/// It uses Avalonia's native RequestAnimationFrame to drive physics and visual updates.
/// </summary>
public class VisualElementParticleHost<T>(VisualElementEffectWindow owner, int maxPoolSize) : Canvas where T : Control, IVisualElementParticle, new()
{
    // All currently running particles (both frame-driven and compositor-driven).
    private readonly List<T> _activeParticles = [];
    private readonly List<T> _frameDrivenParticles = [];
    private readonly HashSet<T> _pendingFirstFrame = [];

    // A bounded object pool to reuse particles and avoid GC pressure
    private readonly Stack<T> _particlePool = new(maxPoolSize);

    public bool HasActiveParticles => _activeParticles.Count > 0;

    // State flags for the animation loop
    private bool _isAnimating;
    private TimeSpan _lastFrameTime = TimeSpan.Zero;

    /// <summary>
    /// Spawns a new particle (either from the internal pool or by allocating a new one) 
    /// and ensures the animation loop is running. Must be called from the UI thread.
    /// </summary>
    public void SpawnParticle(
        Point startPosition,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize)
    {
        if (!_particlePool.TryPop(out var particle))
        {
            particle = new T();
            Children.Add(particle); // Add to VisualTree immediately. Pool elements remain in tree but are hidden.
        }

        // Hidden controls report an empty desired size. Let the pooled control participate in layout while
        // keeping it non-drawing until all content, geometry, and animation state have been reset.
        particle.Opacity = 0d;
        particle.IsVisible = true;
        try
        {
            particle.Spawn(owner, startPosition, targetTracker, startContent, endContent, startSize);
        }
        catch
        {
            particle.IsVisible = false;
            particle.RegisterCompletionCallback(null);
            particle.Recycle();
            if (_particlePool.Count < maxPoolSize) _particlePool.Push(particle);
            else Children.Remove(particle);
            throw;
        }
        particle.Opacity = 1d;
        _activeParticles.Add(particle);

        if (particle.IsSelfAnimating)
        {
            particle.RegisterCompletionCallback(() => CompleteParticle(particle));
        }
        else
        {
            _frameDrivenParticles.Add(particle);
            _pendingFirstFrame.Add(particle);
            StartAnimationLoop();
        }
    }

    /// <summary>
    /// Clears all particles immediately, completely stopping the animation
    /// and returning them to the pool.
    /// </summary>
    public void ClearParticles()
    {
        foreach (var particle in _activeParticles)
        {
            particle.RegisterCompletionCallback(null);
            particle.IsVisible = false;
            particle.Recycle();
            if (_particlePool.Count < maxPoolSize)
            {
                _particlePool.Push(particle);
            }
            else
            {
                Children.Remove(particle); // Overflow drops out of the visual tree
            }
        }

        _activeParticles.Clear();
        _frameDrivenParticles.Clear();
        _pendingFirstFrame.Clear();
        _isAnimating = false;
    }

    private void CompleteParticle(T particle)
    {
        if (!_activeParticles.Remove(particle)) return;

        particle.RegisterCompletionCallback(null);
        _frameDrivenParticles.Remove(particle);
        _pendingFirstFrame.Remove(particle);
        particle.IsVisible = false;
        particle.Recycle();

        if (_particlePool.Count < maxPoolSize)
        {
            _particlePool.Push(particle);
        }
        else
        {
            // Pool is full, destroy the excess control
            Children.Remove(particle);
        }

        if (_activeParticles.Count == 0)
        {
            owner.HandleHostIdle();
        }
    }

    /// <summary>
    /// Bootstraps the VSync-aligned animation loop if it's not already running.
    /// </summary>
    private void StartAnimationLoop()
    {
        if (_isAnimating) return;

        _isAnimating = true;
        _lastFrameTime = TimeSpan.Zero; // Reset the clock

        // Request the first frame from the compositor
        owner.RequestAnimationFrame(OnAnimationFrame);
    }

    /// <summary>
    /// The core animation loop callback, invoked by the Avalonia compositor right before drawing.
    /// </summary>
    /// <param name="time">The total elapsed time provided by the compositor.</param>
    private void OnAnimationFrame(TimeSpan time)
    {
        if (!_isAnimating) return;

        if (_lastFrameTime == TimeSpan.Zero) _lastFrameTime = time;
        var deltaTimeMs = Math.Clamp((time - _lastFrameTime).TotalMilliseconds, 0d, 100d);
        _lastFrameTime = time;

        for (var i = _frameDrivenParticles.Count - 1; i >= 0; i--)
        {
            var particle = _frameDrivenParticles[i];
            var frameDelta = _pendingFirstFrame.Remove(particle) ? 0d : deltaTimeMs;
            if (particle.Update(frameDelta))
            {
                CompleteParticle(particle);
            }
        }

        if (_frameDrivenParticles.Count > 0)
        {
            // Schedule next frame if still active
            owner.RequestAnimationFrame(OnAnimationFrame);
        }
        else
        {
            _isAnimating = false;
        }
    }
}