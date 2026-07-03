using Avalonia.Controls.Notifications;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Collections;
using Everywhere.Messages;
using Everywhere.Skills;
using Everywhere.Views;
using MessagePack;
using ZLinq;

namespace Everywhere.ViewModels;

public enum PromptEditorMode
{
    Create,
    Edit
}

/// <summary>
/// Backing model for the standalone advanced prompt editor view.
/// </summary>
/// <remarks>
/// The editor is opened with explicit parameters instead of living inside <see cref="PromptPageViewModel"/>.
/// This mirrors transient views such as the changelog page: Prompt Manager browsing remains one page,
/// while create/edit owns its own navigation lifetime and save/cancel flow.
/// </remarks>
public sealed partial class PromptEditorViewModel(
    IPromptService promptService,
    ISkillPromptProvider skillPromptProvider
) : BusyViewModelBase
{
    private static readonly TimeSpan PreviewRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly SystemPromptPlaceholderSource PlaceholderSource = SystemPromptPlaceholderSource.Instance;

    private readonly BindableList<PromptDiagnosticItem> _diagnostics = [];
    private DispatcherTimer? _previewRefreshTimer;
    private Guid? _editingPromptId;
    private string? _originalName;
    private string _originalTemplate = string.Empty;
    private PromptSource _originalSource = PromptSource.Blank;
    private byte[]? _originalMetadataPayload;
    private string _cancelRoute = MainViewNavigateMessage.PromptPageRoute;

    public IReadOnlyBindableList<PromptDiagnosticItem> Diagnostics => _diagnostics;

    public IReadOnlyList<PlaceholderReferenceItem> PlaceholderReferences { get; } =
        PlaceholderReferenceItem.CreateDefaultItems(PlaceholderSource.Definitions);

    public IDynamicLocaleKey TitleKey => Mode switch
    {
        PromptEditorMode.Edit => new DynamicLocaleKey(LocaleKey.PromptEditor_EditTitle),
        _ => new DynamicLocaleKey(LocaleKey.PromptEditor_CreateTitle)
    };

    public bool IsDirty =>
        !StringComparer.Ordinal.Equals(NormalizeName(Name), NormalizeName(_originalName)) ||
        !StringComparer.Ordinal.Equals(Template, _originalTemplate);

    public bool CanSave =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(Template) &&
        (Mode == PromptEditorMode.Create || (_editingPromptId.HasValue && IsDirty));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleKey))]
    public partial PromptEditorMode Mode { get; private set; } = PromptEditorMode.Create;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Template { get; set; } = DefaultPrompts.DefaultSystemPrompt;

    [ObservableProperty]
    public partial string RenderedPreview { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<PromptTemplateRenderSegment> RenderedPreviewSegments { get; private set; } = [];

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
    /// Opens the editor for a new blank advanced prompt.
    /// </summary>
    public void OpenForCreate()
    {
        Mode = PromptEditorMode.Create;
        _editingPromptId = null;
        _originalName = null;
        _originalTemplate = string.Empty;
        _originalSource = PromptSource.Blank;
        _originalMetadataPayload = null;
        _cancelRoute = MainViewNavigateMessage.PromptPageRoute;

        Name = null;
        Template = DefaultPrompts.DefaultSystemPrompt;
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
                    new DynamicLocaleKey(LocaleKey.PromptEditor_OpenFailedToast_Title),
                    new DynamicLocaleKey(LocaleKey.PromptEditor_OpenFailedToast_Content))
                .DismissOnClick()
                .ShowWarning();

            NavigateToPromptPage();
            return false;
        }

        Mode = PromptEditorMode.Edit;
        _editingPromptId = prompt.Id;
        _originalName = prompt.Name;
        _originalTemplate = prompt.Template;
        _originalSource = prompt.Source;
        _originalMetadataPayload = prompt.MetadataPayload?.ToArray();
        _cancelRoute = MainViewNavigateMessage.ToPrompt(prompt.Id);

        Name = prompt.Name;
        Template = prompt.Template;
        RefreshEditorOutput();
        NotifySaveStateChanged();
        return true;
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnTemplateChanged(string value)
    {
        RefreshEditorOutput();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnModeChanged(PromptEditorMode value)
    {
        NotifySaveStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        try
        {
            var savedPrompt = Mode == PromptEditorMode.Create ?
                await promptService.CreatePromptAsync(new PromptCreateRequest(Template, NormalizeName(Name))) :
                await SaveExistingPromptAsync();

            if (savedPrompt is null)
            {
                ToastHost
                    .CreateToast(new DynamicLocaleKey(LocaleKey.PromptEditor_SaveFailedToast_Title))
                    .DismissOnClick()
                    .ShowWarning();

                NavigateToPromptPage();
                return;
            }

            WeakReferenceMessenger.Default.Send(
                new MainViewNavigateMessage(MainViewNavigateMessage.ToPrompt(savedPrompt.Id)));
        }
        catch (Exception ex)
        {
            ToastHost
                .CreateToast(new DynamicLocaleKey(LocaleKey.PromptEditor_SaveFailedToast_Title), ex.GetFriendlyMessage())
                .DismissOnClick()
                .ShowError();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(_cancelRoute));
    }

    private async Task<PromptDefinition?> SaveExistingPromptAsync()
    {
        if (_editingPromptId is not { } promptId)
        {
            return null;
        }

        return await promptService.UpdatePromptAsync(
            promptId,
            new PromptUpdateRequest(
                Template,
                NormalizeName(Name),
                CreateUpdatedMetadataPayload()));
    }

    private byte[]? CreateUpdatedMetadataPayload()
    {
        if (_originalSource != PromptSource.Guided || _originalMetadataPayload is null)
        {
            return _originalMetadataPayload?.ToArray();
        }

        try
        {
            var snapshot = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(_originalMetadataPayload);
            snapshot.IsDetachedFromRecipe = true;
            return MessagePackSerializer.Serialize(snapshot);
        }
        catch
        {
            return [.. _originalMetadataPayload];
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

        RenderedPreview = renderResult.RenderedText;
        RenderedPreviewSegments = renderResult.Segments;
        _diagnostics.Reset(
            renderResult.Diagnostics
                .AsValueEnumerable()
                .Select(static diagnostic => new PromptDiagnosticItem(diagnostic))
                .ToList());
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
        new(SkillsPromptResolver: skillPromptProvider.GetPrompt);

    private void NotifySaveStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static void NavigateToPromptPage()
    {
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(MainViewNavigateMessage.PromptPageRoute));
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

    public sealed record PlaceholderReferenceItem(
        string PlaceholderText,
        IDynamicLocaleKey DescriptionKey,
        IBrush PlaceholderBrush
    )
    {
        public static IReadOnlyList<PlaceholderReferenceItem> CreateDefaultItems(
            IReadOnlyList<PromptPlaceholderDefinition> definitions)
        {
            var colorSlots = PromptPlaceholderPalette.AssignColorSlots([.. definitions.Select(static definition => definition.Name)]);
            return
            [
                .. definitions
                    .Select(definition => new PlaceholderReferenceItem(
                        "{" + definition.Name + "}",
                        definition.DescriptionKey,
                        PromptPlaceholderPalette.GetBrush(definition.Name, colorSlots[definition.Name]))),
            ];
        }
    }
}
