using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class HomePage : ReactiveUserControl<HomePageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 0;

    public LucideIconKind Icon => LucideIconKind.House;

    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.HomePage_Title);

    public HomePage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}
