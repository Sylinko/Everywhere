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
    /// Formats a byte count in powers of 1024 (B, KiB, MiB, GiB or TiB).
    /// For example, 1024 becomes "1 KiB" and 1048576 becomes "1 MiB".
    /// </summary>
    /// <param name="bytes">Unscaled byte count, not a value already expressed in KiB or MiB.</param>
    /// <returns>The scaled value with at most two decimal places and its binary unit.</returns>
    public static string HumanizeBytes(long bytes)
    {
        var sizes = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var len = (double)bytes;
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}