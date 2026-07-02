using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Utilities;
using Everywhere.Views;
using ShadUI;

namespace Everywhere.ViewModels;

public abstract class ReactiveViewModelBase : ObservableValidator, IDisposable
{
    [field: AllowNull, MaybeNull]
    protected DialogManager DialogManager
    {
        get => field ??= ServiceLocator.Resolve<DialogManager>();
        private set;
    }

    [field: AllowNull, MaybeNull]
    protected ToastHost ToastHost
    {
        get => field ??= ServiceLocator.Resolve<ToastHost>();
        private set;
    }

    protected IClipboard Clipboard =>
        _topLevel?.Clipboard ?? throw new InvalidOperationException("Clipboard is not available.");

    protected IStorageProvider StorageProvider =>
        _topLevel?.StorageProvider ?? throw new InvalidOperationException("StorageProvider is not available.");

    protected static ILauncher Launcher => BetterBclLauncher.Shared;

    protected AnonymousExceptionHandler DialogExceptionHandler => new((exception, message, _, _) =>
        DialogManager.CreateDialog(
            exception.GetFriendlyMessage().ToString() ?? LocaleResolver.Common_Unknown,
            message ?? LocaleResolver.Common_Error));

    protected AnonymousExceptionHandler ToastExceptionHandler => new((exception, message, _, _) =>
        ToastHost.CreateToast(message ?? LocaleResolver.Common_Error)
            .WithContent(exception.GetFriendlyMessage())
            .DismissOnClick()
            .ShowError());

    protected CompositeDisposable LifetimeDisposables { get; } = new();

    protected virtual IExceptionHandler? LifetimeExceptionHandler => null;

    private bool _isLoaded;
    private bool _isDisposed;
    private TopLevel? _topLevel;

    /// <summary>
    /// Invoked when the view's <see cref="Control.Loaded"/> event is raised.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected internal virtual Task ViewLoaded(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Invoked when the view's <see cref="Control.Unloaded"/> event is raised.
    /// </summary>
    /// <returns></returns>
    protected internal virtual Task ViewUnloaded() => Task.CompletedTask;

    /// <summary>
    /// Invoked when main navigation targets this view model.
    /// </summary>
    /// <param name="remainingSegments">Decoded path segments after the matched main navigation route.</param>
    protected internal virtual void OnNavigatedTo(IReadOnlyList<string> remainingSegments) { }

    private void HandleLifetimeException(string stage, Exception e)
    {
        var handler = LifetimeExceptionHandler ?? DialogManager.ToExceptionHandler();
        handler.HandleException(e, $"Lifetime Exception: [{stage}]");
    }

    public void Bind(Control target, bool disposeOnUnloaded = false)
    {
        target.DataContext = this;
        CancellationTokenSource? cancellationTokenSource = null;

        // ReSharper disable once AsyncVoidEventHandlerMethod
        async void LoadedHandler(object? sender, RoutedEventArgs args)
        {
            var cancellationToken = CancellationToken.None;
            try
            {
                if (_isDisposed || _isLoaded) return;

                cancellationTokenSource?.Dispose();
                cancellationTokenSource = new CancellationTokenSource();
                cancellationToken = cancellationTokenSource.Token;

                _isLoaded = true;
                _topLevel = TopLevel.GetTopLevel(target);

                if (_topLevel is IReactiveHost reactiveHost)
                {
                    DialogManager = reactiveHost.DialogHost.Manager;
                    ToastHost = reactiveHost.ToastHost;
                }
                await ViewLoaded(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                HandleLifetimeException(nameof(ViewLoaded), e);
            }
        }

        async void UnloadedHandler(object? sender, RoutedEventArgs args)
        {
            try
            {
                if (!_isLoaded) return;

                _isLoaded = false;
                if (cancellationTokenSource is not null)
                {
                    await cancellationTokenSource.CancelAsync();
                }

                try
                {
                    await ViewUnloaded();
                }
                finally
                {
                    _topLevel = null;
                    DisposeHelper.DisposeToDefault(ref cancellationTokenSource);
                }

                if (disposeOnUnloaded)
                {
                    Dispose();
                }
            }
            catch (Exception e)
            {
                HandleLifetimeException(nameof(ViewUnloaded), e);
            }
        }

        target.Loaded += LoadedHandler;
        target.Unloaded += UnloadedHandler;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || _isDisposed)
            return;

        _isDisposed = true;
        LifetimeDisposables.Dispose();
    }
}
