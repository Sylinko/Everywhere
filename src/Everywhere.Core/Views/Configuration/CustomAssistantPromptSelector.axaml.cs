using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Messages;
using Everywhere.Skills;
using Everywhere.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Everywhere.Views;

/// <summary>
/// Selects and previews a Prompt Manager prompt.
/// </summary>
/// <remarks>
/// The selector owns the prompt list and resolves <see cref="SelectedId"/> through
/// <see cref="IPromptService"/>. Its timer refreshes dynamic values such as <c>{Time}</c>;
/// template lookup only runs when the selected prompt changes.
/// </remarks>
[TemplatePart(Name = ComboBoxPartName, Type = typeof(ComboBox), IsRequired = true)]
public sealed partial class CustomAssistantPromptSelector(CustomAssistant customAssistant, IServiceProvider serviceProvider) : TemplatedControl
{
    private const string ComboBoxPartName = "PART_ComboBox";
    private static readonly TimeSpan PreviewRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly SystemPromptPlaceholderSource PlaceholderSource = SystemPromptPlaceholderSource.Instance;

    public static readonly StyledProperty<Guid> SelectedIdProperty =
        AvaloniaProperty.Register<CustomAssistantPromptSelector, Guid>(nameof(SelectedId), enableDataValidation: true);

    public static readonly StyledProperty<string> RawTemplateProperty =
        AvaloniaProperty.Register<CustomAssistantPromptSelector, string>(nameof(RawTemplate), string.Empty);

    public static readonly StyledProperty<IReadOnlyList<PromptTemplateRenderSegment>> RenderedPreviewSegmentsProperty =
        AvaloniaProperty.Register<CustomAssistantPromptSelector, IReadOnlyList<PromptTemplateRenderSegment>>(nameof(RenderedPreviewSegments), []);

    /// <summary>
    /// Selected prompt ID. <see cref="Guid.Empty"/> selects the built-in default prompt.
    /// </summary>
    public Guid SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    /// <summary>
    /// Prompt options displayed by the selector.
    /// </summary>
    public IReadOnlyBindableList<Item> ItemsSource => _items;

    public string RawTemplate
    {
        get => GetValue(RawTemplateProperty);
        private set => SetValue(RawTemplateProperty, value);
    }

    public IReadOnlyList<PromptTemplateRenderSegment> RenderedPreviewSegments
    {
        get => GetValue(RenderedPreviewSegmentsProperty);
        private set => SetValue(RenderedPreviewSegmentsProperty, value);
    }

    private readonly ISkillPromptProvider _skillPromptProvider = serviceProvider.GetRequiredService<ISkillPromptProvider>();
    private readonly IPromptService _promptService = serviceProvider.GetRequiredService<IPromptService>();
    private readonly BindableList<Item> _items = [];

    private ComboBox? _comboBox;
    private DispatcherTimer? _previewRefreshTimer;
    private CancellationTokenSource? _refreshCancellationTokenSource;
    private CancellationTokenSource? _loadCancellationTokenSource;
    private string _template = string.Empty;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedIdProperty && VisualRoot is not null)
        {
            QueueLoadTemplate();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _comboBox = e.NameScope.Find<ComboBox>(ComboBoxPartName);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        QueueRefreshPrompts();
        QueueLoadTemplate();
        StartPreviewRefreshTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopPreviewRefreshTimer();
        CancelRefreshPrompts();
        CancelLoadTemplate();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void UpdateDataValidation(AvaloniaProperty property, BindingValueType state, Exception? error)
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
        RefreshPromptsAsync(_refreshCancellationTokenSource.Token).Detach();
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
            var prompts = await _promptService.ListPromptsAsync(cancellationToken);
            _items.Reset(prompts.Select(static prompt => Item.FromPrompt(prompt)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Logger.ForContext<CustomAssistantPromptSelector>().Warning(
                HandledSystemException.Handle(ex),
                "Failed to load prompts for assistant prompt selector.");
        }
    }

    private void QueueLoadTemplate()
    {
        CancelLoadTemplate();
        _loadCancellationTokenSource = new CancellationTokenSource();
        LoadTemplateAsync(_loadCancellationTokenSource.Token).Detach();
    }

    private void CancelLoadTemplate()
    {
        _loadCancellationTokenSource?.Cancel();
        _loadCancellationTokenSource?.Dispose();
        _loadCancellationTokenSource = null;
    }

    private async Task LoadTemplateAsync(CancellationToken cancellationToken)
    {
        var promptId = SelectedId;
        try
        {
            var prompt = await _promptService.GetPromptAsync(promptId, cancellationToken) ?? _promptService.DefaultPrompt;
            RawTemplate = _template = prompt.Template;
            RefreshRenderedPreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Logger.ForContext<CustomAssistantPromptSelector>().Warning(
                HandledSystemException.Handle(ex),
                "Failed to render assistant prompt preview for prompt {PromptId}.",
                promptId);
        }
    }

    private void RefreshRenderedPreview()
    {
        RenderedPreviewSegments = PromptTemplateRenderer.RenderSegments(_template, PlaceholderSource, CreatePromptContext());
    }

    private void StartPreviewRefreshTimer()
    {
        if (_previewRefreshTimer is not null)
        {
            return;
        }

        _previewRefreshTimer = new DispatcherTimer
        {
            Interval = PreviewRefreshInterval
        };
        _previewRefreshTimer.Tick += HandlePreviewRefreshTimerTick;
        _previewRefreshTimer.Start();
    }

    private void StopPreviewRefreshTimer()
    {
        if (_previewRefreshTimer is null)
        {
            return;
        }

        _previewRefreshTimer.Tick -= HandlePreviewRefreshTimerTick;
        _previewRefreshTimer.Stop();
        _previewRefreshTimer = null;
    }

    private void HandlePreviewRefreshTimerTick(object? sender, EventArgs e)
    {
        RefreshRenderedPreview();
    }

    private PromptPlaceholderContext CreatePromptContext() =>
        new(SkillsPromptResolver: () => _skillPromptProvider.GetPrompt(customAssistant.ToolCallStatus));

    /// <summary>
    /// Display model for a prompt option.
    /// </summary>
    public sealed record Item(Guid Id, IDynamicLocaleKey DisplayNameKey)
    {
        public static Item FromPrompt(PromptDefinition prompt) => new(prompt.Id, PromptDisplayNameProvider.GetDisplayNameKey(prompt));
    }
}
