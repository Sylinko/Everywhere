using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class ChatPluginPage : ReactiveUserControl<ChatPluginPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 1;

    public LucideIconKind Icon => LucideIconKind.Hammer;

    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.ChatPluginPage_Title);

    public ChatPluginPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}