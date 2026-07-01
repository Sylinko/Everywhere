using System.Diagnostics;

namespace Everywhere.Extensions;

public static class DebugExtensions
{
    extension<T>(T target)
    {
        [Conditional("DEBUG")]
        public void Debug(Action<T>? peek = null)
        {
            Debugger.Break();
            peek?.Invoke(target);
        }

        [Conditional("DEBUG")]
        public void DebugWriteLineWithDateTime()
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {target}");
        }
    }
}