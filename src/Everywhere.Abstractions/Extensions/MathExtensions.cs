namespace Everywhere.Extensions;

public static class MathExtensions
{
    public static bool IsCloseTo(this double value, double target, double tolerance = 0.0001)
    {
        return Math.Abs(value - target) < tolerance;
    }

    public static bool IsCloseTo(this float value, float target, float tolerance = 0.0001f)
    {
        return Math.Abs(value - target) < tolerance;
    }

    public static T Clamp<T>(this T value, T min, T max) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0) return min;
        if (value.CompareTo(max) > 0) return max;
        return value;
    }

    public static float Lerp(this float from, float to, float t)
    {
        return from + (to - from) * t.Clamp(0f, 1f);
    }

    public static double Lerp(this double from, double to, double t)
    {
        return from + (to - from) * t.Clamp(0.0, 1.0);
    }

    public static float SmoothStep(this float from, float to, float t)
    {
        t = t.Clamp(0f, 1f);
        t = t * t * (3f - 2f * t);
        return from.Lerp(to, t);
    }

    public static double SmoothStep(this double from, double to, double t)
    {
        t = t.Clamp(0.0, 1.0);
        t = t * t * (3.0 - 2.0 * t);
        return from.Lerp(to, t);
    }
}