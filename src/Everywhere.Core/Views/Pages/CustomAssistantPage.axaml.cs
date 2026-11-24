using Tabler.Avalonia;

namespace Everywhere.Views.Pages;

public partial class CustomAssistantPage : ReactiveUserControl<CustomAssistantPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 0;

    public TablerIconKind Icon => TablerIconKind.MessageChatbot;

    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.CustomAssistantPage_Title);

    public CustomAssistantPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}