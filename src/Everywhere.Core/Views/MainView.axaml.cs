namespace Everywhere.Views;

public partial class MainView : ReactiveUserControl<MainViewModel>
{
    public MainView(FloatingIslandWindow floatingIslandWindow)
    {
        InitializeComponent();

        floatingIslandWindow.Show();
    }
}