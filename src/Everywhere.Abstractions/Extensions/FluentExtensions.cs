namespace Everywhere.Extensions;

public static class FluentExtensions
{
    public static T With<T>(this T t, Action<T> action)
    {
        action(t);
        return t;
    }
}