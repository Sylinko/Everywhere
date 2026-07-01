namespace Everywhere.Views;

public partial class MainView : ReactiveUserControl<MainViewModel>
{
    public MainView(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}