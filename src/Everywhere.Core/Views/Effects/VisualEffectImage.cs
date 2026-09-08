namespace Everywhere.Views;

/// <summary>Shares one owned image between a producer, multiple screen particles, and retained drawing operations.</summary>
/// <remarks>Each participant adds one reference before the producer releases its own, and disposes exactly once when finished.</remarks>
public sealed class VisualEffectImage<T>(T image) : IDisposable where T : class, IDisposable
{
    /// <summary>Gets the image while a reference remains alive.</summary>
    public T? Image => _image;

    private T? _image = image;
    private int _referenceCount = 1;

    /// <summary>Adds ownership for another participant while an existing reference is held.</summary>
    public void AddRef() => Interlocked.Increment(ref _referenceCount);

    /// <summary>Releases this participant's reference, disposing the image after its final user.</summary>
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _referenceCount) != 0) return;
        Interlocked.Exchange(ref _image, null)?.Dispose();
    }
}