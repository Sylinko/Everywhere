using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Views;

public interface IReactiveView
{
    ReactiveViewModelBase ViewModel { get; }
}

public abstract class ReactiveUserControl<TViewModel> : UserControl, IReactiveView where TViewModel : ReactiveViewModelBase
{
    public TViewModel ViewModel { get; }

    ReactiveViewModelBase IReactiveView.ViewModel => ViewModel;

    protected ReactiveUserControl(IServiceProvider serviceProvider, bool disposeOnUnloaded = true)
    {
        ViewModel = serviceProvider.GetRequiredService<TViewModel>();
        ViewModel.Bind(this, disposeOnUnloaded);
    }
}

public abstract class ReactiveShadWindow<TViewModel> : ShadWindow, IReactiveView where TViewModel : ReactiveViewModelBase
{
    public TViewModel ViewModel { get; }

    ReactiveViewModelBase IReactiveView.ViewModel => ViewModel;

    protected ReactiveShadWindow(IServiceProvider serviceProvider, bool disposeOnUnloaded = true)
    {
        ViewModel = serviceProvider.GetRequiredService<TViewModel>();
        ViewModel.Bind(this, disposeOnUnloaded);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel.Dispose();
    }
}