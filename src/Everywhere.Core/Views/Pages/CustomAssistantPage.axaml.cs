using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class CustomAssistantPage : ReactiveUserControl<CustomAssistantPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 0;

    public LucideIconKind Icon => LucideIconKind.Bot;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.CustomAssistantPage_Title);

    public CustomAssistantPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}