using Windows.Win32.System.Com;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Owns exactly one reference to a COM object and releases it deterministically.
/// </summary>
/// <remarks>
/// Multiple instances may refer to the same COM object. Each instance independently owns the one reference transferred to its constructor.
/// </remarks>
public abstract unsafe class ComReference : IDisposable
{
    private nint _pointer;

    private protected ComReference(nint pointer) => _pointer = pointer;

    /// <summary>
    /// Releases this instance's owned COM reference exactly once.
    /// </summary>
    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0)
        {
            ((IUnknown*)pointer)->Release();
        }
    }

    private protected T* GetPointer<T>() where T : unmanaged
    {
        var pointer = Volatile.Read(ref _pointer);
        ObjectDisposedException.ThrowIf(pointer == 0, this);
        return (T*)pointer;
    }
}