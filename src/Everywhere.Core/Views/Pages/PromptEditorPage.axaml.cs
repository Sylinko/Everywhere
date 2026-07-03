namespace Everywhere.Views.Pages;

public sealed partial class PromptEditorPage : ReactiveUserControl<PromptEditorViewModel>
{
    public PromptEditorPage(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        InitializeComponent();
    }
}