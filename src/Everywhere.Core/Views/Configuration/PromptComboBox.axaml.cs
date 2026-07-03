using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI.Prompts;
using Everywhere.Collections;
using Everywhere.Messages;
using Everywhere.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Everywhere.Views;

[TemplatePart(Name = ComboBoxPartName, Type = typeof(ComboBox), IsRequired = true)]
public sealed partial class PromptComboBox(IPromptService promptService, IServiceProvider serviceProvider) : TemplatedControl
{
    private const string ComboBoxPartName = "PART_ComboBox";

    public static readonly StyledProperty<Guid> SelectedIdProperty =
        AvaloniaProperty.Register<PromptComboBox, Guid>(nameof(SelectedId), enableDataValidation: true);

    private readonly BindableList<PromptComboBoxItem> _items = [];
    private ComboBox? _comboBox;
    private CancellationTokenSource? _refreshCancellationTokenSource;

    /// <summary>
    /// Selected prompt ID. <see cref="Guid.Empty"/> means the built-in default prompt.
    /// </summary>
    public Guid SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <summary>
    /// Prompt options shown by the selector.
    /// </summary>
    public IReadOnlyBindableList<PromptComboBoxItem> ItemsSource => _items;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _comboBox = e.NameScope.Find<ComboBox>(ComboBoxPartName);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        QueueRefreshPrompts();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelRefreshPrompts();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void UpdateDataValidation(
        AvaloniaProperty property,
        BindingValueType state,
        Exception? error)
    {
        if (property == SelectedIdProperty && _comboBox is not null)
        {
            DataValidationErrors.SetError(_comboBox, error);
        }
    }

    [RelayCommand]
    private void CreatePrompt()
    {
        var editorView = serviceProvider.GetRequiredService<PromptEditorPage>();
        editorView.ViewModel.OpenForCreate();
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(editorView));
    }

    [RelayCommand]
    private void ManagePrompts()
    {
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(MainViewNavigateMessage.ToPrompt(SelectedId)));
    }

    private void QueueRefreshPrompts()
    {
        CancelRefreshPrompts();
        _refreshCancellationTokenSource = new CancellationTokenSource();
        _ = RefreshPromptsAsync(_refreshCancellationTokenSource.Token);
    }

    private void CancelRefreshPrompts()
    {
        _refreshCancellationTokenSource?.Cancel();
        _refreshCancellationTokenSource?.Dispose();
        _refreshCancellationTokenSource = null;
    }

    private async Task RefreshPromptsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var prompts = await promptService.ListPromptsAsync(cancellationToken);
            _items.Reset(prompts.Select(static prompt => PromptComboBoxItem.FromPrompt(prompt)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Logger.ForContext<PromptComboBox>().Warning(ex, "Failed to load prompts for assistant prompt selector.");
        }
    }
}

public sealed record PromptComboBoxItem(Guid Id, IDynamicLocaleKey DisplayNameKey)
{
    public static PromptComboBoxItem FromPrompt(PromptDefinition prompt) =>
        new(
            prompt.Id,
            PromptDisplayNameProvider.GetDisplayNameKey(prompt));
}
