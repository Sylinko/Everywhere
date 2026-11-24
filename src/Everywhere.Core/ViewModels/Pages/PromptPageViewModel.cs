using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Collections;
using Everywhere.Configuration;
using Everywhere.Messages;
using Everywhere.Skills;
using Everywhere.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.ViewModels;

/// <summary>
/// Manages prompt selection, previews, and edits from the Prompt Manager page.
/// </summary>
public sealed partial class PromptPageViewModel(
    IPromptService promptService,
    Settings settings,
    ISkillPromptProvider skillPromptProvider,
    IServiceProvider serviceProvider
) : BusyViewModelBase
{
    private static readonly SystemPromptPlaceholderSource PlaceholderSource = SystemPromptPlaceholderSource.Shared;

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
    public IReadOnlyBindableList<CustomAssistant> SelectedPromptReferences => _selectedPromptReferences;

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
    [NotifyCanExecuteChangedFor(nameof(CopyRenderedPreviewCommand))]
    public partial PromptItem? SelectedPromptItem { get; set; }

    [ObservableProperty]
    public partial IDynamicLocaleKey FilteredPromptCountKey { get; private set; } =
        new FormattedDynamicLocaleKey(LocaleKey.PromptPage_CountText, new DirectLocaleKey(0));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyRenderedPreviewCommand))]
    public partial string RenderedPreview { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<PromptTemplateRenderSegment> RenderedPreviewSegments { get; private set; } = [];

    [ObservableProperty]
    public partial string RawTemplate { get; private set; } = string.Empty;

    private readonly BindableList<PromptItem> _prompts = [];
    private readonly BindableList<PromptItem> _filteredPrompts = [];
    private readonly BindableList<PromptDiagnosticItem> _selectedPromptDiagnostics = [];
    private readonly BindableList<CustomAssistant> _selectedPromptReferences = [];

    private bool _hasLoadedPrompts;
    private Guid? _pendingRoutePromptId;

    /// <inheritdoc />
    protected internal override async Task ViewLoaded(CancellationToken cancellationToken)
    {
        await ExecuteBusyTaskAsync(LoadPromptsAsync, ToastExceptionHandler, cancellationToken);
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
                    LocaleKey.PromptPage_NavigationWarning_Title.I18N(),
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
            ExecuteBusyTaskAsync(LoadPromptsAsync, ToastExceptionHandler).Detach(ToastExceptionHandler);
        }
    }

    partial void OnSelectedPromptItemChanged(PromptItem? value)
    {
        RefreshSelectedPromptDetails(value);
    }

    [RelayCommand]
    private void CreatePrompt()
    {
        var editorView = serviceProvider.GetRequiredService<PromptEditorPage>();
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

        var editorView = serviceProvider.GetRequiredService<PromptEditorPage>();
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

        var references = settings.FindAssistantsUsingPrompt(prompt.Id);
        var result = await DialogHost.CreateDialog(
                CreateDeletePromptDialogMessage(prompt, references.Length),
                LocaleKey.Common_Warning.I18N())
            .WithPrimaryButton(LocaleKey.Common_Delete.I18N(), buttonStyle: ButtonStyle.Destructive)
            .WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync();
        if (result != DialogResult.Primary)
        {
            return;
        }

        await ExecuteBusyTaskAsync(
            async cancellationToken =>
            {
                if (!await promptService.DeletePromptAsync(prompt.Id, cancellationToken))
                {
                    RefreshSelectedPromptDetails(prompt);
                    return;
                }

                // Assignments may have changed while the dialog or database operation was pending.
                foreach (var assistant in settings.FindAssistantsUsingPrompt(prompt.Id))
                {
                    assistant.SystemPromptId = Guid.Empty;
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
            0 => LocaleKey.PromptPage_DeletePrompt_Dialog_Message.I18N().Format(prompt.DisplayName),
            1 => LocaleKey.PromptPage_DeletePromptUsedByAssistant_Dialog_Message.I18N().Format(prompt.DisplayName),
            _ => LocaleKey.PromptPage_DeletePromptUsedByAssistants_Dialog_Message.I18N().Format(referenceCount, prompt.DisplayName)
        };

    private bool CanCopyRenderedPreview => SelectedPromptItem is not null && !string.IsNullOrEmpty(RenderedPreview);

    [RelayCommand(CanExecute = nameof(CanCopyRenderedPreview))]
    private async Task CopyRenderedPreviewAsync()
    {
        if (!CanCopyRenderedPreview) return;
        await App.Clipboard.SetTextAsync(RenderedPreview);
    }

    [RelayCommand]
    private static void OpenAssistantReference(CustomAssistant? assistant)
    {
        if (assistant is null) return;

        WeakReferenceMessenger.Default.Send(
            new MainViewNavigateMessage(MainViewNavigateMessage.ToCustomAssistant(assistant.Id)));
    }

    private async Task LoadPromptsAsync(CancellationToken cancellationToken)
    {
        var selectedPromptId = _pendingRoutePromptId ?? SelectedPromptItem?.Id;
        var shouldWarnForPendingRoute = _pendingRoutePromptId.HasValue;
        _pendingRoutePromptId = null;

        var prompts = await promptService.ListPromptsAsync(cancellationToken);
        var previewContext = CreatePromptContext();
        _prompts.Reset(
            prompts.Select(prompt => new PromptItem(
                prompt,
                PromptTemplateRenderer.Render(prompt.Template, PlaceholderSource, previewContext).TrimStart())));
        _hasLoadedPrompts = true;

        RefreshFilter(clearSelectionWhenHidden: false);

        if (selectedPromptId is { } promptId)
        {
            if (!SelectPrompt(promptId) && shouldWarnForPendingRoute)
            {
                ToastHost
                    .CreateToast(
                        LocaleKey.PromptPage_NavigationWarning_Title.I18N(),
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
            .ToArray();
        _filteredPrompts.Reset(filteredPrompts);

        FilteredPromptCountKey = new FormattedDynamicLocaleKey(
            LocaleKey.PromptPage_CountText,
            new DirectLocaleKey(filteredPrompts.Length));
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
                .ToArray());
        _selectedPromptReferences.Reset(settings.FindAssistantsUsingPrompt(promptItem.Id));
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

    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        OnPropertyChanged(nameof(CanDeleteSelectedPrompt));
        DeleteSelectedPromptCommand.NotifyCanExecuteChanged();
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
        new(SkillsPromptResolver: () => skillPromptProvider.GetPrompt(ToolCallStatus.Enabled));

    public sealed class PromptItem(PromptDefinition prompt, string renderedPreview)
    {
        public Guid Id => prompt.Id;

        public string Template => prompt.Template;

        public string RenderedPreview { get; } = renderedPreview;

        public bool IsDefault => prompt.IsDefault;

        public bool IsBuiltIn => prompt.IsBuiltIn;

        public IDynamicLocaleKey DisplayNameKey => PromptDisplayNameProvider.GetDisplayNameKey(prompt);

        public string DisplayName => PromptDisplayNameProvider.GetDisplayName(prompt);

        public IReadOnlyList<string> SearchValues { get; } =
        [
            prompt.Id.ToString("D"),
            PromptDisplayNameProvider.GetDisplayName(prompt),
            prompt.Name ?? string.Empty,
            prompt.Template,
            renderedPreview,
            prompt.IsDefault ? LocaleKey.PromptPage_DefaultPrompt_DisplayName.I18N() : string.Empty,
            prompt.IsDefault ? LocaleKey.PromptPage_Source_BuiltInDefault.I18N() : ResolveSourceLabelOrFallback(prompt.Source),
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