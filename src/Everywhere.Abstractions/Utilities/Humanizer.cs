using System.Globalization;
using System.Numerics;

namespace Everywhere.Utilities;

public static class Humanizer
{
    /// <summary>
    /// Converts a number into a human-readable string with appropriate suffixes (K for thousands, M for millions, B for billions).
    /// </summary>
    /// <param name="number"></param>
    /// <param name="format"></param>
    /// <param name="cultureInfo"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static string HumanizeNumber<T>(T number, string format = "N0", CultureInfo? cultureInfo = null) where T : INumber<T>
    {
        T baseNumber;
        var abs = T.Abs(number);
        if (abs >= (baseNumber = T.CreateTruncating(1_000_000_000))) return $"{(number / baseNumber).ToString(format, cultureInfo)}B";
        if (abs >= (baseNumber = T.CreateTruncating(1_000_000))) return $"{(number / baseNumber).ToString(format, cultureInfo)}M";
        if (abs >= (baseNumber = T.CreateTruncating(1_000))) return $"{(number / baseNumber).ToString(format, cultureInfo)}K";
        return number.ToString(format, cultureInfo);
    }

    /// <summary>
    /// Converts a byte size into a human-readable string with appropriate units.
    /// e.g., 1024 -> "1 KB", 1048576 -> "1 MB"
    /// </summary>
    /// <param name="bytes"></param>
    /// <returns></returns>
    public static string HumanizeBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}