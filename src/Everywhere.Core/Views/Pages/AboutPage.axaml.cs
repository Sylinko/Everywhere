using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public sealed partial class AboutPage : ReactiveUserControl<AboutPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => int.MaxValue;

    public LucideIconKind Icon => LucideIconKind.Info;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.AboutPage_Title);

    public AboutPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}