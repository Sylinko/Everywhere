using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// A lightweight host for control-based particles.
/// It uses Avalonia's native RequestAnimationFrame to drive physics and visual updates.
/// </summary>
public class VisualElementParticleHost<T>(VisualElementEffectWindow owner, int maxPoolSize) : Canvas where T : VisualElementParticle, new()
{
    // A list to hold all currently animating particles
    private readonly List<T> _activeParticles = [];
    
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

        particle.Spawn(startPosition, targetTracker, startContent, endContent, startSize);
        particle.IsVisible = true;
        _activeParticles.Add(particle);

        StartAnimationLoop();
    }

    /// <summary>
    /// Clears all particles immediately, completely stopping the animation
    /// and returning them to the pool.
    /// </summary>
    public void ClearParticles()
    {
        foreach (var particle in _activeParticles)
        {
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
        _isAnimating = false;
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
        var deltaTimeMs = (time - _lastFrameTime).TotalMilliseconds;
        _lastFrameTime = time;

        for (var i = _activeParticles.Count - 1; i >= 0; i--)
        {
            var particle = _activeParticles[i];
            if (particle.Update(deltaTimeMs))
            {
                // Particle has reached the end of its lifecycle
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
                
                _activeParticles.RemoveAt(i);
            }
        }

        if (_activeParticles.Count > 0)
        {
            // Schedule next frame if still active
            owner.RequestAnimationFrame(OnAnimationFrame);
        }
        else
        {
            _isAnimating = false;
            owner.HandleHostIdle();
        }
    }
}