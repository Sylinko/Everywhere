using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class SkillPage : ReactiveUserControl<SkillPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 2;

    public LucideIconKind Icon => LucideIconKind.Sparkles;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SkillPage_Title);

    public SkillPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}
