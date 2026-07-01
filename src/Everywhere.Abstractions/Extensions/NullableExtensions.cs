using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Extensions;

public static class NullableExtensions
{
    /// <summary>
    /// 将一个可能为空的转成不可空，如果为null将抛出<see cref="NullReferenceException"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="t"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    public static T NotNull<T>([NotNull] this T? t, string? message = null) where T : notnull => 
        t ?? throw new NullReferenceException(message);

    public async static ValueTask<T> NotNullAsync<T>(this ValueTask<T?> t, string? message = null) where T : notnull =>
        await t ?? throw new NullReferenceException(message);

    /// <summary>
    /// 将一个可能为空的转成不可空，如果为null将抛出<see cref="NullReferenceException"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TException"></typeparam>
    /// <param name="t"></param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    public static T NotNull<T, TException>([NotNull] this T? t) where T : notnull where TException : Exception, new() => 
        t ?? throw new TException();

    /// <summary>
    /// 将一个可能为空的转成不可空，如果为null将抛出<see cref="NullReferenceException"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="t"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException"></exception>
    public static T NotNull<T>([NotNull] this object? t, string? message = null) where T : notnull
    {
        if (t is T result) return result;
        throw new NullReferenceException(message);
    }
}