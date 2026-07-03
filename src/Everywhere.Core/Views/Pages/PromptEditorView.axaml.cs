namespace Everywhere.Views.Pages;

public sealed partial class PromptEditorView : ReactiveUserControl<PromptEditorViewModel>
{
    public PromptEditorView(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        InitializeComponent();
    }
}