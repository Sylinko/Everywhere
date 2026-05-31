using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class ChatPluginPage : ReactiveUserControl<ChatPluginPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 1;

    public LucideIconKind Icon => LucideIconKind.Hammer;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.ChatPluginPage_Title);

    public ChatPluginPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}