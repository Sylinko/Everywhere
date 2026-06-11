using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class WebSearchEnginePage : ReactiveUserControl<WebSearchEnginePageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 3;

    public LucideIconKind Icon => LucideIconKind.Search;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.WebSearchPage_Title);

    public WebSearchEnginePage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}