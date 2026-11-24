using Tabler.Avalonia;

namespace Everywhere.Views.Pages;

public partial class ChatPluginPage : ReactiveUserControl<ChatPluginPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 2;

    public TablerIconKind Icon => TablerIconKind.Tool;

    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.ChatPluginPage_Title);

    public ChatPluginPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}