using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Views;

public abstract class ReactiveUserControl<TViewModel> : UserControl where TViewModel : ReactiveViewModelBase
{
    public TViewModel ViewModel { get; }

    protected ReactiveUserControl(IServiceProvider serviceProvider, bool disposeOnUnloaded = true)
    {
        ViewModel = serviceProvider.GetRequiredService<TViewModel>();
        ViewModel.Bind(this, disposeOnUnloaded);
    }
}

public abstract class ReactiveShadWindow<TViewModel> : ShadWindow where TViewModel : ReactiveViewModelBase
{
    public TViewModel ViewModel { get; }

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