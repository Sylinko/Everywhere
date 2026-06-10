using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

public partial class SpeechRecognitionPage : ReactiveUserControl<SpeechRecognitionPageViewModel>, IMainViewNavigationTopLevelItem
{
    public int Index => 3;

    public LucideIconKind Icon => LucideIconKind.MicVocal;

    public IDynamicResourceKey TitleKey { get; } = new DynamicResourceKey(LocaleKey.SpeechRecognitionPage_Title);

    public SpeechRecognitionPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        InitializeComponent();
    }
}