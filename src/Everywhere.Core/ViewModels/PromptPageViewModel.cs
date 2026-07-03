using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI.Prompts;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Messages;
using Everywhere.Skills;
using Everywhere.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;
using ZLinq;

namespace Everywhere.ViewModels;

/// <summary>
/// Read-only backing model for the first Prompt Manager page.
/// </summary>
/// <remarks>
/// The page intentionally does not create, edit, or delete prompts yet. Its job is to make the
/// already-established prompt domain visible, prove route selection and preview rendering, and give
/// later editor work a stable layout to extend.
/// </remarks>
public sealed partial class PromptPageViewModel(
    IPromptService promptService,
    IAssistantPromptReferenceService assistantPromptReferenceService,
    ISkillPromptProvider skillPromptProvider,
    IServiceProvider serviceProvider
) : BusyViewModelBase
{
    private static readonly TimeSpan PreviewRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly SystemPromptPlaceholderSource PlaceholderSource = SystemPromptPlaceholderSource.Instance;

    private readonly BindableList<PromptItem> _prompts = [];
    private readonly BindableList<PromptItem> _filteredPrompts = [];
    private readonly BindableList<PromptDiagnosticItem> _selectedPromptDiagnostics = [];
    private readonly BindableList<AssistantPromptReference> _selectedPromptReferences = [];

    private bool _hasLoadedPrompts;
    private Guid? _pendingRoutePromptId;
    private DispatcherTimer? _previewRefreshTimer;

    /// <summary>
    /// Prompts matching the current search text. The built-in default prompt remains a normal item
    /// here, but it is supplied by <see cref="IPromptService"/> as a virtual definition.
    /// </summary>
    public IReadOnlyBindableList<PromptItem> FilteredPrompts => _filteredPrompts;

    /// <summary>
    /// Diagnostics for the currently selected prompt template.
    /// </summary>
    public IReadOnlyBindableList<PromptDiagnosticItem> SelectedPromptDiagnostics => _selectedPromptDiagnostics;

    /// <summary>
    /// Assistants that currently use the selected prompt.
    /// </summary>
    public IReadOnlyBindableList<AssistantPromptReference> SelectedPromptReferences => _selectedPromptReferences;

    public bool HasAnyPrompts => _prompts.Count > 0;

    public string? SearchText
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            RefreshFilter();
        }
    }

    public bool CanEditSelectedPrompt => SelectedPromptItem is { IsBuiltIn: false };

    public bool CanDeleteSelectedPrompt => SelectedPromptItem is { IsBuiltIn: false } && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSelectedPrompt))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedPrompt))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedPromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedPromptCommand))]
    public partial PromptItem? SelectedPromptItem { get; set; }

    [ObservableProperty]
    public partial IDynamicLocaleKey FilteredPromptCountKey { get; private set; } =
        new FormattedDynamicLocaleKey(LocaleKey.PromptPage_CountText, new DirectLocaleKey(0));

    [ObservableProperty]
    public partial string RenderedPreview { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<PromptTemplateRenderSegment> RenderedPreviewSegments { get; private set; } = [];

    [ObservableProperty]
    public partial string RawTemplate { get; private set; } = string.Empty;

    /// <inheritdoc />
    protected internal override async Task ViewLoaded(CancellationToken cancellationToken)
    {
        await ExecuteBusyTaskAsync(LoadPromptsAsync, ToastExceptionHandler, cancellationToken);

        if (!cancellationToken.IsCancellationRequested)
        {
            StartPreviewRefreshTimer();
        }
    }

    /// <inheritdoc />
    protected internal override Task ViewUnloaded()
    {
        StopPreviewRefreshTimer();
        return base.ViewUnloaded();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Navigation can arrive before the async prompt list has loaded. In that case the prompt ID is
    /// kept as a pending route target and applied immediately after the first successful load.
    /// </remarks>
    protected internal override void OnNavigatedTo(IReadOnlyList<string> remainingSegments)
    {
        if (remainingSegments.Count == 0)
        {
            return;
        }

        var promptIdText = remainingSegments[0];
        if (!Guid.TryParse(promptIdText, out var promptId))
        {
            _pendingRoutePromptId = null;
            ClearSelectedPrompt();
            ToastHost
                .CreateToast(
                    LocaleResolver.PromptPage_NavigationWarning_Title,
                    new FormattedDynamicLocaleKey(
                        LocaleKey.PromptPage_InvalidRoutePrompt_Content,
                        new DirectLocaleKey(promptIdText)))
                .DismissOnClick()
                .ShowWarning();
            return;
        }

        if (!_hasLoadedPrompts)
        {
            _pendingRoutePromptId = promptId;
            return;
        }

        if (!SelectPrompt(promptId))
        {
            _pendingRoutePromptId = promptId;
            ExecuteBusyTaskAsync(LoadPromptsAsync, ToastExceptionHandler)
                .Detach(ToastExceptionHandler);
        }
    }

    partial void OnSelectedPromptItemChanged(PromptItem? value)
    {
        RefreshSelectedPromptDetails(value);
    }

    [RelayCommand]
    private void CreatePrompt()
    {
        var editorView = serviceProvider.GetRequiredService<PromptEditorView>();
        editorView.ViewModel.OpenForCreate();
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(editorView));
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedPrompt))]
    private async Task EditSelectedPromptAsync()
    {
        if (SelectedPromptItem is not { IsBuiltIn: false } prompt)
        {
            return;
        }

        var editorView = serviceProvider.GetRequiredService<PromptEditorView>();
        if (await editorView.ViewModel.OpenForEditAsync(prompt.Id))
        {
            WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(editorView));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedPrompt))]
    private async Task DeleteSelectedPromptAsync()
    {
        if (SelectedPromptItem is not { IsBuiltIn: false } prompt)
        {
            return;
        }

        var references = assistantPromptReferenceService.ListReferences(prompt.Id);
        var result = await DialogManager.CreateDialog(
                CreateDeletePromptDialogMessage(prompt, references.Count),
                LocaleResolver.Common_Warning)
            .WithPrimaryButton(LocaleResolver.Common_Delete, buttonStyle: ButtonStyle.Destructive)
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync();
        if (result != DialogResult.Primary)
        {
            return;
        }

        await ExecuteBusyTaskAsync(
            async cancellationToken =>
            {
                assistantPromptReferenceService.ResetReferencesToDefault(prompt.Id);
                if (!await promptService.DeletePromptAsync(prompt.Id, cancellationToken))
                {
                    RefreshSelectedPromptDetails(prompt);
                    return;
                }

                ClearSelectedPrompt();
                await LoadPromptsAsync(cancellationToken);
            },
            ToastExceptionHandler);
    }

    /// <summary>
    /// Creates a stronger confirmation message when deleting the prompt will reset assistant prompt
    /// references. This mirrors the actual delete behavior so users see the effect before confirming.
    /// </summary>
    private static string CreateDeletePromptDialogMessage(PromptItem prompt, int referenceCount) =>
        referenceCount switch
        {
            0 => LocaleResolver.PromptPage_DeletePrompt_Dialog_Message.Format(prompt.DisplayName),
            1 => LocaleResolver.PromptPage_DeletePromptUsedByAssistant_Dialog_Message.Format(prompt.DisplayName),
            _ => LocaleResolver.PromptPage_DeletePromptUsedByAssistants_Dialog_Message.Format(referenceCount, prompt.DisplayName)
        };

    [RelayCommand]
    private async Task CopyRenderedPreviewAsync()
    {
        if (SelectedPromptItem is null || string.IsNullOrEmpty(RenderedPreview)) return;

        try
        {
            await App.Clipboard.SetTextAsync(RenderedPreview);
            ShowCopiedToast();
        }
        catch (Exception ex)
        {
            ToastHost
                .CreateToast(
                    LocaleResolver.PromptPage_CopyFailedToast_Title,
                    HandledSystemException.Handle(ex).GetFriendlyMessage())
                .DismissOnClick()
                .ShowError();
        }
    }

    [RelayCommand]
    private static void OpenAssistantReference(AssistantPromptReference? reference)
    {
        if (reference is null) return;

        WeakReferenceMessenger.Default.Send(
            new MainViewNavigateMessage(MainViewNavigateMessage.ToCustomAssistant(reference.Id)));
    }

    private async Task LoadPromptsAsync(CancellationToken cancellationToken)
    {
        var selectedPromptId = _pendingRoutePromptId ?? SelectedPromptItem?.Id;
        var shouldWarnForPendingRoute = _pendingRoutePromptId.HasValue;
        _pendingRoutePromptId = null;

        var prompts = await promptService.ListPromptsAsync(cancellationToken);
        _prompts.Reset(prompts.Select(static prompt => new PromptItem(prompt)));
        _hasLoadedPrompts = true;

        RefreshFilter(clearSelectionWhenHidden: false);

        if (selectedPromptId is { } promptId)
        {
            if (!SelectPrompt(promptId) && shouldWarnForPendingRoute)
            {
                ToastHost
                    .CreateToast(
                        LocaleResolver.PromptPage_NavigationWarning_Title,
                        new FormattedDynamicLocaleKey(
                            LocaleKey.PromptPage_MissingRoutePrompt_Content,
                            new DirectLocaleKey(promptId)))
                    .DismissOnClick()
                    .ShowWarning();
            }
        }
        else
        {
            ClearSelectedPrompt();
        }
    }

    private void RefreshFilter(bool clearSelectionWhenHidden = true)
    {
        var filteredPrompts = _prompts
            .AsValueEnumerable()
            .Where(FilterPrompt)
            .ToList();
        _filteredPrompts.Reset(filteredPrompts);

        FilteredPromptCountKey = new FormattedDynamicLocaleKey(
            LocaleKey.PromptPage_CountText,
            new DirectLocaleKey(filteredPrompts.Count));
        OnPropertyChanged(nameof(HasAnyPrompts));

        if (clearSelectionWhenHidden &&
            SelectedPromptItem is { } selectedPrompt &&
            filteredPrompts.AsValueEnumerable().All(item => item.Id != selectedPrompt.Id))
        {
            ClearSelectedPrompt();
        }
    }

    private bool FilterPrompt(PromptItem prompt)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        return prompt.SearchValues
            .AsValueEnumerable()
            .Any(value => value.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    private bool SelectPrompt(Guid promptId)
    {
        if (_prompts.AsValueEnumerable().FirstOrDefault(p => p.Id == promptId) is not { } prompt)
        {
            ClearSelectedPrompt();
            return false;
        }

        if (!FilterPrompt(prompt))
        {
            SearchText = null;
        }

        SelectedPromptItem =
            _filteredPrompts.AsValueEnumerable().FirstOrDefault(item => item.Id == promptId) ??
            prompt;
        return true;
    }

    private void ClearSelectedPrompt()
    {
        SelectedPromptItem = null;
    }

    private void RefreshSelectedPromptDetails(PromptItem? promptItem)
    {
        if (promptItem is null)
        {
            ClearRenderedPreview();
            RawTemplate = string.Empty;
            _selectedPromptDiagnostics.Clear();
            _selectedPromptReferences.Clear();
            return;
        }

        var renderResult = PromptTemplateRenderer.RenderWithDiagnostics(
            promptItem.Template,
            PlaceholderSource,
            CreatePromptContext());

        ApplyRenderedPreview(renderResult.RenderedText, renderResult.Segments);
        RawTemplate = promptItem.Template;
        _selectedPromptDiagnostics.Reset(
            renderResult.Diagnostics
                .AsValueEnumerable()
                .Select(static diagnostic => new PromptDiagnosticItem(diagnostic))
                .ToList());
        _selectedPromptReferences.Reset(assistantPromptReferenceService.ListReferences(promptItem.Id));
    }

    /// <summary>
    /// Refreshes only the rendered preview so time-sensitive placeholders such as <c>{Time}</c>
    /// stay current without reloading references or static template diagnostics every tick.
    /// </summary>
    private void RefreshRenderedPreview()
    {
        if (SelectedPromptItem is not { } promptItem)
        {
            return;
        }

        var segments = PromptTemplateRenderer.RenderSegments(
            promptItem.Template,
            PlaceholderSource,
            CreatePromptContext());

        ApplyRenderedPreview(
            string.Concat(segments.Select(static segment => segment.Text)),
            segments);
    }

    private void ApplyRenderedPreview(
        string renderedText,
        IReadOnlyList<PromptTemplateRenderSegment> segments)
    {
        RenderedPreview = renderedText;
        RenderedPreviewSegments = segments;
    }

    private void ClearRenderedPreview()
    {
        RenderedPreview = string.Empty;
        RenderedPreviewSegments = [];
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

    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        OnPropertyChanged(nameof(CanDeleteSelectedPrompt));
        DeleteSelectedPromptCommand.NotifyCanExecuteChanged();
    }

    private void ShowCopiedToast()
    {
        ToastHost
            .CreateToast(LocaleResolver.Common_Copied)
            .DismissOnClick()
            .ShowSuccess();
    }

    /// <summary>
    /// Resolves the preview variables that are also available to runtime system prompt rendering.
    /// </summary>
    /// <remarks>
    /// This preview context deliberately stops short of creating a <see cref="Chat.ChatContext"/>.
    /// Values that are normally chat-scoped, such as the working directory, use a deterministic
    /// application-level preview value until a future picker/editor context owns richer state.
    /// </remarks>
    private PromptPlaceholderContext CreatePromptContext() =>
        new(SkillsPromptResolver: skillPromptProvider.GetPrompt);

    public sealed class PromptItem(PromptDefinition prompt)
    {
        public PromptDefinition Prompt { get; } = prompt;

        public Guid Id => Prompt.Id;

        public string Template => Prompt.Template;

        public bool IsDefault => Prompt.IsDefault;

        public bool IsBuiltIn => Prompt.IsBuiltIn;

        public IDynamicLocaleKey DisplayNameKey =>
            string.IsNullOrWhiteSpace(Prompt.Name) ?
                new DynamicLocaleKey(
                    Prompt.IsDefault ?
                        LocaleKey.PromptPage_DefaultPrompt_DisplayName :
                        LocaleKey.PromptPage_UntitledPrompt_DisplayName) :
                new DirectLocaleKey(Prompt.Name);

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Prompt.Name) ?
                Prompt.IsDefault ?
                    LocaleResolver.PromptPage_DefaultPrompt_DisplayName :
                    LocaleResolver.PromptPage_UntitledPrompt_DisplayName :
                Prompt.Name;

        public IReadOnlyList<string> SearchValues { get; } =
        [
            prompt.Id.ToString("D"),
            prompt.Name ?? string.Empty,
            prompt.Template,
            prompt.IsDefault ? LocaleResolver.PromptPage_DefaultPrompt_DisplayName : string.Empty,
            prompt.IsDefault ? LocaleResolver.PromptPage_Source_BuiltInDefault : ResolveSourceLabelOrFallback(prompt.Source),
            prompt.Source.ToString()
        ];

        private static string ResolveSourceLabelOrFallback(PromptSource source)
        {
            try
            {
                return source.I18N();
            }
            catch (InvalidOperationException)
            {
                return source.ToString();
            }
        }
    }

    public sealed record PromptDiagnosticItem(PromptDiagnostic Diagnostic)
    {
        public IDynamicLocaleKey MessageKey => Diagnostic.MessageKey;

        public NotificationType Type => Diagnostic.Severity switch
        {
            PromptDiagnosticSeverity.Error => NotificationType.Error,
            PromptDiagnosticSeverity.Warning => NotificationType.Warning,
            _ => NotificationType.Information
        };
    }
}