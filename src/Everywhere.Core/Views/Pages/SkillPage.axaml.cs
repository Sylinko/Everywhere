using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class SkillPage : ReactiveUserControl<SkillPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 3;

    public LucideIconKind Icon => LucideIconKind.Box;

    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SkillPage_Title);

    public SkillPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}
