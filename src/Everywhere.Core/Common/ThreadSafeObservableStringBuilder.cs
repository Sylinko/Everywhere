using System.Text;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;

namespace Everywhere.Common;

/// <summary>
/// A thread-safe wrapper around <see cref="ObservableStringBuilder"/> that allows appending from any thread.
/// </summary>
public sealed class ThreadSafeObservableStringBuilder : ObservableStringBuilder
{
    private readonly Lock _lock = new();
    private readonly StringBuilder _source = new();

    public new int Length
    {
        get
        {
            using var _ = _lock.EnterScope();
            return _source.Length;
        }
    }

    public new ThreadSafeObservableStringBuilder Append(string? value)
    {
        if (string.IsNullOrEmpty(value)) return this;

        using (_lock.EnterScope())
        {
            _source.Append(value);
        }

        Dispatcher.UIThread.PostOnDemand(() => base.Append(value), DispatcherPriority.Normal);
        return this;
    }

    public new ThreadSafeObservableStringBuilder AppendLine(string? value = null)
    {
        if (string.IsNullOrEmpty(value)) return this;

        using (_lock.EnterScope())
        {
            _source.AppendLine(value);
        }

        Dispatcher.UIThread.PostOnDemand(() => base.AppendLine(value), DispatcherPriority.Normal);
        return this;
    }

    public new ThreadSafeObservableStringBuilder Clear()
    {
        using (_lock.EnterScope())
        {
            _source.Clear();
        }

        Dispatcher.UIThread.PostOnDemand(() => base.Clear(), DispatcherPriority.Normal);
        return this;
    }

    public override string ToString()
    {
        using var _ = _lock.EnterScope();
        return _source.ToString();
    }
}