using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;

namespace Everywhere.ViewModels;

public abstract partial class BusyViewModelBase : ReactiveViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; private set; }

    public bool IsNotBusy => !IsBusy;

    private readonly SemaphoreSlim _executionLock = new(1, 1);

    protected async Task ExecuteBusyTaskAsync(
        Func<CancellationToken, Task> task,
        IExceptionHandler? exceptionHandler = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _executionLock.WaitAsync(0, cancellationToken)) return;

        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            IsBusy = true;

            await task(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore
        }
        catch (Exception e) when (exceptionHandler != null)
        {
            exceptionHandler.HandleException(e);
        }
        finally
        {
            IsBusy = false;
            _executionLock.Release();
        }
    }

    protected Task ExecuteBusyTaskAsync(
        Action<CancellationToken> action,
        IExceptionHandler? exceptionHandler = null,
        CancellationToken cancellationToken = default) => ExecuteBusyTaskAsync(
        token =>
        {
            action(token);
            return Task.CompletedTask;
        },
        exceptionHandler,
        cancellationToken);

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnIsBusyChanged(bool value) => OnIsBusyChanged();

    /// <summary>
    /// Invoked when the value of <see cref="IsBusy"/> changes.
    /// </summary>
    protected virtual void OnIsBusyChanged() { }
}