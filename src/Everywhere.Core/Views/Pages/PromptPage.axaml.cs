using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class PromptPage : ReactiveUserControl<PromptPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 1;

    public LucideIconKind Icon => LucideIconKind.FileText;

    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.PromptPage_Title);

    public PromptPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}
