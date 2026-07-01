namespace Everywhere.Extensions;

public static class DoubleExtensions
{
    /// <summary>
    /// Returns the value if it is finite; otherwise, returns the specified default value.
    /// </summary>
    /// <param name="value"></param>
    /// <param name="defaultValue"></param>
    /// <returns></returns>
    public static double FiniteOrDefault(this double value, double defaultValue = 0d)
    {
        return double.IsFinite(value) ? value : defaultValue;
    }
}