using Everywhere.Common;

namespace Everywhere.Extensions;

public static class TaskExtensions
{
    /// <summary>
    /// 将一个Task与异常处理器绑定，当Task抛出异常时，异常会被处理器处理。如果没有指定异常处理器，则会抛出异常。
    /// </summary>
    /// <param name="task"></param>
    /// <param name="exceptionHandler"></param>
    /// <param name="message"></param>
    /// <param name="source"></param>
    public static async void Detach(
        this Task task,
        IExceptionHandler? exceptionHandler = null,
        string? message = null,
        [CallerMemberName] object? source = null)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (exceptionHandler != null)
        {
            exceptionHandler.HandleException(e, message, source);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Detach<T>(
        this Task<T> task,
        IExceptionHandler? exceptionHandler = null,
        string? message = null,
        [CallerMemberName] string? source = null) =>
        Detach((Task)task, exceptionHandler, message, source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Detach(
        this ValueTask task,
        IExceptionHandler? exceptionHandler = null,
        string? message = null,
        [CallerMemberName] string? source = null) =>
        Detach(task.AsTask(), exceptionHandler, message, source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Detach<T>(
        this ValueTask<T> task,
        IExceptionHandler? exceptionHandler = null,
        string? message = null,
        [CallerMemberName] string? source = null) => Detach((Task)task.AsTask(), exceptionHandler, message, source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask ToValueTask(this Task task) => new(task);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask<T> ToValueTask<T>(this Task<T> task) => new(task);
}