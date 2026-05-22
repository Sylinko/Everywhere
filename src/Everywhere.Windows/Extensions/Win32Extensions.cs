using Windows.Win32.Foundation;

namespace Everywhere.Windows.Extensions;

public static class Win32Extensions
{
    /// <summary>
    /// Throws a Win32Exception if the HRESULT indicates a failure.
    /// </summary>
    /// <param name="hResult"></param>
    public static void ThrowOnFailure(this int hResult)
    {
        ((HRESULT)hResult).ThrowOnFailure();
    }
}