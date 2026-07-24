using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Messages;
using Everywhere.Skills;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.Extensions.Logging;
using Serilog;
using ShadUI;

namespace Everywhere.ViewModels;

/// <summary>
/// Backing model for the unified prompt authoring surface.
/// </summary>
/// <remarks>
/// The page keeps only one transient editing flag: <see cref="IsAdvancedEditing"/>. Persistent
/// meaning stays in <see cref="PromptSource"/> and <see cref="PromptRecipeSnapshot"/> metadata,
/// so create and edit can share the same save path without introducing another mode enum.
/// </remarks>
public sealed partial class PromptEditorViewModel(
    IPromptService promptService,
    ISkillPromptProvider skillPromptProvider,
    ILogger<PromptEditorViewModel> logger
) : BusyViewModelBase
{
    private static readonly TimeSpan PreviewRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly SystemPromptPlaceholderSource PlaceholderSource = SystemPromptPlaceholderSource.Instance;

    public IReadOnlyBindableList<PromptDiagnosticItem> Diagnostics => _diagnostics;

    public IReadOnlyList<PromptPlaceholderReferenceItem> PlaceholderReferences { get; } =
        PromptPlaceholderReferenceItem.CreateDefaultItems(PlaceholderSource.Definitions);

    public IReadOnlyList<PromptRecipeOptionItem> PersonaOptions { get; } =
        CreateItems(PromptRecipeCatalog.Personas);

    public IReadOnlyList<PromptRecipeOptionItem> ScenarioOptions { get; } =
        CreateItems(PromptRecipeCatalog.Scenarios);

    public IReadOnlyList<PromptRecipeOptionItem> ToneOptions { get; } =
        CreateItems(PromptRecipeCatalog.Tones);

    public IReadOnlyList<PromptRecipeOptionItem> DetailOptions { get; } =
        CreateItems(PromptRecipeCatalog.DetailLevels);

    public IReadOnlyList<PromptRecipeOptionItem> OrganizationOptions { get; } =
        CreateItems(PromptRecipeCatalog.Organizations);

    public IDynamicLocaleKey TitleKey => EditingPromptId.HasValue ?
        new DynamicLocaleKey(LocaleKey.PromptEditor_EditTitle) :
        new DynamicLocaleKey(LocaleKey.PromptEditor_CreateTitle);

    public bool IsQuickMode => !IsAdvancedEditing;

    public bool IsAdvancedMode => IsAdvancedEditing;

    public bool IsDirty => EditingPromptId is null || IsSaveDraftDirty(CreateSaveDraft());

    public bool CanSave =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(Template) &&
        (EditingPromptId is null || IsDirty);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleKey))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial Guid? EditingPromptId { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickMode))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedMode))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsAdvancedEditing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? Name { get; set; }

    [ObservableProperty]
    public partial PromptRecipeOptionItem? SelectedPersonaOption { get; set; }

    [ObservableProperty]
    public partial PromptRecipeOptionItem? SelectedToneOption { get; set; }

    [ObservableProperty]
    public partial PromptRecipeOptionItem? SelectedDetailOption { get; set; }

    [ObservableProperty]
    public partial PromptRecipeOptionItem? SelectedOrganizationOption { get; set; }

    [ObservableProperty]
    public partial string? PreferredUserName { get; set; }

    [ObservableProperty]
    public partial string? AdditionalRequirements { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Template { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<PromptTemplateRenderSegment> RenderedPreviewSegments { get; private set; } = [];

    private readonly BindableList<PromptDiagnosticItem> _diagnostics = [];
    private DispatcherTimer? _previewRefreshTimer;
    private bool _isLoadingRecipe;
    private string? _originalName;
    private string _originalTemplate = string.Empty;
    private PromptSource _originalSource = PromptSource.Blank;
    private byte[]? _originalMetadataPayload;
    private PromptRecipeSnapshot? _recipeSnapshot;
    private string _cancelRoute = MainViewNavigateMessage.PromptPageRoute;

    /// <inheritdoc />
    protected internal override Task ViewLoaded(CancellationToken cancellationToken)
    {
        StartPreviewRefreshTimer();
        return base.ViewLoaded(cancellationToken);
    }

    /// <inheritdoc />
    protected internal override Task ViewUnloaded()
    {
        StopPreviewRefreshTimer();
        return base.ViewUnloaded();
    }

    /// <summary>
    /// Opens the editor for a new recipe-backed prompt.
    /// </summary>
    public void OpenForCreate()
    {
        EditingPromptId = null;
        _originalName = null;
        _originalTemplate = string.Empty;
        _originalSource = PromptSource.Blank;
        _originalMetadataPayload = null;
        _cancelRoute = MainViewNavigateMessage.PromptPageRoute;

        Name = null;
        LoadRecipeSnapshot(PromptRecipeCatalog.CreateDefaultSnapshot());
        IsAdvancedEditing = false;
        ApplyQuickTemplate();
        RefreshEditorOutput();
        NotifySaveStateChanged();
    }

    /// <summary>
    /// Opens the editor for an existing user prompt.
    /// </summary>
    /// <returns>False when the target is missing or immutable and the editor should not be shown.</returns>
    public async Task<bool> OpenForEditAsync(Guid promptId, CancellationToken cancellationToken = default)
    {
        var prompt = await promptService.GetPromptAsync(promptId, cancellationToken);
        if (prompt is null || prompt.IsBuiltIn)
        {
            ToastHost
                .CreateToast(
                    LocaleResolver.PromptEditor_OpenFailedToast_Title,
                    LocaleResolver.PromptEditor_OpenFailedToast_Content)
                .DismissOnClick()
                .ShowWarning();

            NavigateToPromptPage();
            return false;
        }

        EditingPromptId = prompt.Id;
        _originalName = prompt.Name;
        _originalTemplate = prompt.Template;
        _originalSource = prompt.Source;
        _originalMetadataPayload = ClonePayload(prompt.MetadataPayload);
        _cancelRoute = MainViewNavigateMessage.ToPrompt(prompt.Id);

        Name = prompt.Name;
        Template = prompt.Template;

        if (TryReadRecipeSnapshot(prompt, out var snapshot))
        {
            LoadRecipeSnapshot(snapshot);
            IsAdvancedEditing = snapshot.IsDetachedFromRecipe;
        }
        else
        {
            LoadRecipeSnapshot(PromptRecipeCatalog.CreateDefaultSnapshot());
            _recipeSnapshot = null;
            IsAdvancedEditing = true;
        }

        RefreshEditorOutput();
        NotifySaveStateChanged();
        return true;
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSelectedPersonaOptionChanged(PromptRecipeOptionItem? value)
    {
        RefreshSingleSelectionStates();
        ApplyQuickTemplateIfNeeded();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSelectedToneOptionChanged(PromptRecipeOptionItem? value)
    {
        RefreshSingleSelectionStates();
        ApplyQuickTemplateIfNeeded();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSelectedDetailOptionChanged(PromptRecipeOptionItem? value)
    {
        RefreshSingleSelectionStates();
        ApplyQuickTemplateIfNeeded();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSelectedOrganizationOptionChanged(PromptRecipeOptionItem? value)
    {
        RefreshSingleSelectionStates();
        ApplyQuickTemplateIfNeeded();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnPreferredUserNameChanged(string? value)
    {
        ApplyQuickTemplateIfNeeded();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnAdditionalRequirementsChanged(string? value)
    {
        ApplyQuickTemplateIfNeeded();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnTemplateChanged(string value)
    {
        RefreshEditorOutput();
    }

    [RelayCommand]
    private void ToggleScenario(PromptRecipeOptionItem? option)
    {
        if (option is null)
        {
            return;
        }

        if (!option.IsSelected &&
            ScenarioOptions.AsValueEnumerable().Count(static scenario => scenario.IsSelected) >= PromptRecipeCatalog.MaximumScenarioCount)
        {
            return;
        }

        option.IsSelected = !option.IsSelected;
        ApplyQuickTemplateIfNeeded();
    }

    [RelayCommand]
    private void SelectPersona(PromptRecipeOptionItem? option)
    {
        if (option is not null)
        {
            SelectedPersonaOption = option;
        }
    }

    [RelayCommand]
    private void SelectTone(PromptRecipeOptionItem? option)
    {
        if (option is not null)
        {
            SelectedToneOption = option;
        }
    }

    [RelayCommand]
    private void SelectDetail(PromptRecipeOptionItem? option)
    {
        if (option is not null)
        {
            SelectedDetailOption = option;
        }
    }

    [RelayCommand]
    private void SelectOrganization(PromptRecipeOptionItem? option)
    {
        if (option is not null)
        {
            SelectedOrganizationOption = option;
        }
    }

    [RelayCommand]
    private void SwitchToAdvanced()
    {
        _recipeSnapshot = CreateSnapshot(detached: false);
        Template = PromptRecipeCatalog.ComposeTemplate(_recipeSnapshot);
        IsAdvancedEditing = true;
        NotifySaveStateChanged();
    }

    [RelayCommand]
    private async Task SwitchToQuickAsync()
    {
        if (_recipeSnapshot is null)
        {
            LoadRecipeSnapshot(PromptRecipeCatalog.CreateDefaultSnapshot());
        }

        var snapshot = CreateSnapshot(detached: false);
        var quickTemplate = PromptRecipeCatalog.ComposeTemplate(snapshot);
        if (!string.Equals(Template, quickTemplate, StringComparison.Ordinal))
        {
            var result = await DialogHost
                .CreateDialog(
                    LocaleResolver.PromptCreate_DiscardAdvanced_DialogMessage,
                    LocaleResolver.PromptCreate_DiscardAdvanced_DialogTitle)
                .WithPrimaryButton(LocaleResolver.PromptCreate_DiscardAdvanced_DialogPrimary)
                .WithCancelButton(LocaleResolver.Common_Cancel)
                .ShowAsync();
            if (result != DialogResult.Primary)
            {
                return;
            }
        }

        _recipeSnapshot = snapshot;
        IsAdvancedEditing = false;
        Template = quickTemplate;
        NotifySaveStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        await ExecuteBusyTaskAsync(
            async cancellationToken =>
            {
                var draft = CreateSaveDraft();
                var savedPrompt = EditingPromptId is { } promptId ?
                    await promptService.UpdatePromptAsync(
                        promptId,
                        new PromptUpdateRequest(
                            draft.Template,
                            draft.Name,
                            draft.MetadataPayload,
                            draft.Source),
                        cancellationToken) :
                    await promptService.CreatePromptAsync(
                        new PromptCreateRequest(
                            draft.Template,
                            draft.Name,
                            draft.Source,
                            draft.MetadataPayload),
                        cancellationToken);

                if (savedPrompt is null)
                {
                    ToastHost
                        .CreateToast(LocaleResolver.PromptEditor_SaveFailedToast_Title)
                        .DismissOnClick()
                        .ShowWarning();

                    NavigateToPromptPage();
                    return;
                }

                WeakReferenceMessenger.Default.Send(
                    new MainViewNavigateMessage(MainViewNavigateMessage.ToPrompt(savedPrompt.Id)));
            },
            new AnonymousExceptionHandler((exception, _, _, _) =>
            {
                exception = HandledSystemException.Handle(exception);
                logger.LogError(exception, "Failed to save prompt.");

                ToastHost
                    .CreateToast(LocaleResolver.PromptEditor_SaveFailedToast_Title)
                    .WithContent(exception.GetFriendlyMessage())
                    .DismissOnClick()
                    .ShowError();
            }));
    }

    [RelayCommand]
    private void Cancel()
    {
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(_cancelRoute));
    }

    private PromptSaveDraft CreateSaveDraft()
    {
        if (!IsAdvancedEditing)
        {
            var snapshot = CreateSnapshot(detached: false);
            return new PromptSaveDraft(
                PromptRecipeCatalog.ComposeTemplate(snapshot),
                NormalizeName(Name),
                PromptSource.Guided,
                SerializeSnapshot(snapshot));
        }

        if (_recipeSnapshot is not null)
        {
            var snapshot = CreateSnapshot(detached: true);
            return new PromptSaveDraft(
                Template,
                NormalizeName(Name),
                PromptSource.Guided,
                SerializeSnapshot(snapshot));
        }

        return new PromptSaveDraft(
            Template,
            NormalizeName(Name),
            EditingPromptId.HasValue ? _originalSource : PromptSource.Blank,
            EditingPromptId.HasValue ? ClonePayload(_originalMetadataPayload) : null);
    }

    private bool IsSaveDraftDirty(PromptSaveDraft draft) =>
        !StringComparer.Ordinal.Equals(draft.Name, NormalizeName(_originalName)) ||
        !StringComparer.Ordinal.Equals(draft.Template, _originalTemplate) ||
        draft.Source != _originalSource ||
        !PayloadEquals(draft.MetadataPayload, _originalMetadataPayload);

    private void LoadRecipeSnapshot(PromptRecipeSnapshot snapshot)
    {
        var normalized = PromptRecipeCatalog.NormalizeSnapshot(snapshot, snapshot.IsDetachedFromRecipe);
        _recipeSnapshot = normalized;
        _isLoadingRecipe = true;
        try
        {
            SelectedPersonaOption = FindItem(PersonaOptions, normalized.PersonaId) ?? PersonaOptions[0];
            SelectedToneOption = FindItem(ToneOptions, normalized.ToneId) ?? ToneOptions[0];
            SelectedDetailOption = FindItem(DetailOptions, normalized.DetailLevelId) ?? DetailOptions[1];
            SelectedOrganizationOption = FindItem(OrganizationOptions, normalized.OrganizationId) ?? OrganizationOptions[0];
            PreferredUserName = normalized.PreferredUserName;
            AdditionalRequirements = normalized.AdditionalRequirements;

            foreach (var scenario in ScenarioOptions)
            {
                scenario.IsSelected = normalized.ScenarioIds.AsValueEnumerable().Contains(scenario.Id);
            }
        }
        finally
        {
            _isLoadingRecipe = false;
        }

        RefreshSingleSelectionStates();
    }

    private PromptRecipeSnapshot CreateSnapshot(bool detached)
    {
        var snapshot = new PromptRecipeSnapshot
        {
            SchemaVersion = PromptRecipeCatalog.CurrentSnapshotSchemaVersion,
            PersonaId = SelectedPersonaOption?.Id,
            PreferredUserName = PreferredUserName,
            ScenarioIds = [.. ScenarioOptions.AsValueEnumerable().Where(static option => option.IsSelected).Select(static option => option.Id)],
            ToneId = SelectedToneOption?.Id,
            DetailLevelId = SelectedDetailOption?.Id,
            OrganizationId = SelectedOrganizationOption?.Id,
            AdditionalRequirements = AdditionalRequirements
        };

        return PromptRecipeCatalog.NormalizeSnapshot(snapshot, detached);
    }

    private void ApplyQuickTemplateIfNeeded()
    {
        if (!IsAdvancedEditing && !_isLoadingRecipe)
        {
            ApplyQuickTemplate();
        }
    }

    private void ApplyQuickTemplate()
    {
        _recipeSnapshot = CreateSnapshot(detached: false);
        Template = PromptRecipeCatalog.ComposeTemplate(_recipeSnapshot);
        NotifySaveStateChanged();
    }

    private void RefreshSingleSelectionStates()
    {
        RefreshSelectionState(PersonaOptions, SelectedPersonaOption);
        RefreshSelectionState(ToneOptions, SelectedToneOption);
        RefreshSelectionState(DetailOptions, SelectedDetailOption);
        RefreshSelectionState(OrganizationOptions, SelectedOrganizationOption);
    }

    private static void RefreshSelectionState(
        IReadOnlyList<PromptRecipeOptionItem> options,
        PromptRecipeOptionItem? selectedOption)
    {
        foreach (var option in options)
        {
            option.IsSelected = ReferenceEquals(option, selectedOption);
        }
    }

    /// <summary>
    /// Re-renders preview and diagnostics for the current editor buffer.
    /// </summary>
    /// <remarks>
    /// Template edits call this immediately. The timer calls it again so time-sensitive placeholders
    /// such as <c>{Time}</c> keep moving even when the text itself is unchanged.
    /// </remarks>
    private void RefreshEditorOutput()
    {
        var renderResult = PromptTemplateRenderer.RenderWithDiagnostics(
            Template,
            PlaceholderSource,
            CreatePromptContext());

        RenderedPreviewSegments = renderResult.Segments;
        _diagnostics.Reset(
            renderResult.Diagnostics
                .AsValueEnumerable()
                .Select(static diagnostic => new PromptDiagnosticItem(diagnostic))
                .ToArray());
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
        RefreshEditorOutput();
    }

    private PromptPlaceholderContext CreatePromptContext() =>
        new(SkillsPromptResolver: () => skillPromptProvider.GetPrompt(ToolCallStatus.Enabled));

    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        NotifySaveStateChanged();
    }

    private void NotifySaveStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<PromptRecipeOptionItem> CreateItems(IReadOnlyList<PromptRecipeOption> options) =>
        [.. options.Select(static option => new PromptRecipeOptionItem(option))];

    private static PromptRecipeOptionItem? FindItem(IReadOnlyList<PromptRecipeOptionItem> options, string? id) =>
        string.IsNullOrWhiteSpace(id) ?
            null :
            options.AsValueEnumerable().FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal));

    private static bool TryReadRecipeSnapshot(PromptDefinition prompt, out PromptRecipeSnapshot snapshot)
    {
        if (prompt is { Source: PromptSource.Guided, MetadataPayload: { Length: > 0 } payload })
        {
            try
            {
                snapshot = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(payload);
                snapshot = PromptRecipeCatalog.NormalizeSnapshot(snapshot, snapshot.IsDetachedFromRecipe);
                return true;
            }
            catch (Exception ex)
            {
                // Corrupt authoring metadata should not block editing the persisted prompt text.
                Log.ForContext<PromptEditorViewModel>().Warning(HandledSystemException.Handle(ex), "Failed to read recipe snapshot");
            }
        }

        snapshot = PromptRecipeCatalog.CreateDefaultSnapshot();
        return false;
    }

    private static byte[] SerializeSnapshot(PromptRecipeSnapshot snapshot) =>
        MessagePackSerializer.Serialize(snapshot);

    private static byte[]? ClonePayload(byte[]? payload) =>
        payload?.ToArray();

    private static bool PayloadEquals(byte[]? left, byte[]? right) =>
        left is null && right is null ||
        left is not null && right is not null && left.AsSpan().SequenceEqual(right);

    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static void NavigateToPromptPage()
    {
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(MainViewNavigateMessage.PromptPageRoute));
    }

    private sealed record PromptSaveDraft(
        string Template,
        string? Name,
        PromptSource Source,
        byte[]? MetadataPayload
    );

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

public sealed partial class PromptRecipeOptionItem(PromptRecipeOption option) : ObservableObject
{
    public string Id => option.Id;

    public IDynamicLocaleKey NameKey => option.NameKey;

    public LucideIconKind? Icon => option.Icon;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
