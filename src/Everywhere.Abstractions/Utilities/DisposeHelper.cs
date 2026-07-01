namespace Everywhere.Utilities;

public static class DisposeHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DisposeToDefault<T>(ref T? disposable) where T : IDisposable
    {
        if (disposable is null) return;
        disposable.Dispose();
        disposable = default;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FreeHGlobalToNull(ref IntPtr ptr)
    {
        Marshal.FreeHGlobal(ptr);
        ptr = IntPtr.Zero;
    }
}