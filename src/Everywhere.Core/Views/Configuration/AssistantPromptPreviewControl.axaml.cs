using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Common;
using Everywhere.Skills;
using Serilog;

namespace Everywhere.Views;

/// <summary>
/// Read-only settings preview for the prompt selected by a custom assistant.
/// </summary>
/// <remarks>
/// The control observes <see cref="CustomAssistant.SystemPromptId"/> and renders with the same
/// system placeholder source used by Prompt Manager preview. The timer only refreshes rendered
/// values such as <c>{Time}</c>; template lookup runs when the selected prompt changes.
/// </remarks>
public sealed class AssistantPromptPreviewControl(
    CustomAssistant customAssistant,
    IAssistantPromptResolver promptResolver,
    ISkillPromptProvider skillPromptProvider
) : TemplatedControl
{
    private static readonly TimeSpan PreviewRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly SystemPromptPlaceholderSource PlaceholderSource = SystemPromptPlaceholderSource.Instance;

    public static readonly StyledProperty<string> RawTemplateProperty =
        AvaloniaProperty.Register<AssistantPromptPreviewControl, string>(nameof(RawTemplate), string.Empty);

    public static readonly StyledProperty<IReadOnlyList<PromptTemplateRenderSegment>> RenderedPreviewSegmentsProperty =
        AvaloniaProperty.Register<AssistantPromptPreviewControl, IReadOnlyList<PromptTemplateRenderSegment>>(nameof(RenderedPreviewSegments), []);

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

    private DispatcherTimer? _previewRefreshTimer;
    private CancellationTokenSource? _loadCancellationTokenSource;
    private string _template = string.Empty;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        customAssistant.PropertyChanged += HandleAssistantPropertyChanged;
        QueueLoadTemplate();
        StartPreviewRefreshTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopPreviewRefreshTimer();
        CancelLoadTemplate();
        customAssistant.PropertyChanged -= HandleAssistantPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void HandleAssistantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomAssistant.SystemPromptId))
        {
            QueueLoadTemplate();
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
        try
        {
            var resolution = await promptResolver.ResolveSystemPromptAsync(customAssistant, cancellationToken: cancellationToken);
            RawTemplate = _template = resolution.Template;
            RefreshRenderedPreview();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Logger.ForContext<AssistantPromptPreviewControl>().Warning(
                HandledSystemException.Handle(ex),
                "Failed to render assistant prompt preview for assistant {AssistantId}.",
                customAssistant.Id);
        }
    }

    private void RefreshRenderedPreview()
    {
        RenderedPreviewSegments = PromptTemplateRenderer.RenderSegments(
            _template,
            PlaceholderSource,
            CreatePromptContext());
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
        new(SkillsPromptResolver: skillPromptProvider.GetPrompt);
}