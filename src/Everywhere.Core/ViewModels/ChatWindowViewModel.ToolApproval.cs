using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Views;
using Lucide.Avalonia;
using ShadUI;

namespace Everywhere.ViewModels;

partial class ChatWindowViewModel
{
    /// <summary>Gets the stable options shared by the approval menu and effective-mode indicator.</summary>
    public IReadOnlyList<ToolApprovalModeOption> ApprovalModeOptions { get; } =
    [
        new(
            ToolApprovalMode.Ask,
            new DynamicLocaleKey(LocaleKey.ToolApproval_Ask),
            new DynamicLocaleKey(LocaleKey.ToolApproval_AskDescription),
            LucideIconKind.UserCheck),
        new(
            ToolApprovalMode.Auto,
            new DynamicLocaleKey(LocaleKey.ToolApproval_Auto),
            new DynamicLocaleKey(LocaleKey.ToolApproval_AutoDescription),
            LucideIconKind.ShieldCheck),
        new(
            ToolApprovalMode.FullAccess,
            new DynamicLocaleKey(LocaleKey.ToolApproval_FullAccess),
            new DynamicLocaleKey(LocaleKey.ToolApproval_FullAccessDescription),
            LucideIconKind.ShieldAlert)
    ];

    /// <summary>Gets the saved next-turn selection, including while a running turn retains another mode.</summary>
    public ToolApprovalModeOption SelectedApprovalOption => FindApprovalOption(Settings.Plugin.ApprovalMode);

    /// <summary>Gets the effective option for the currently displayed conversation.</summary>
    public ToolApprovalModeOption EffectiveApprovalOption =>
        FindApprovalOption(CurrentApprovalGeneration?.ApprovalMode ?? Settings.Plugin.ApprovalMode);

    /// <summary>Gets the pending transition message without resolving its localization.</summary>
    [ObservableProperty]
    public partial IDynamicLocaleKey PendingApprovalMessageKey { get; private set; } = DirectLocaleKey.Empty;

    /// <summary>Gets whether the displayed turn differs from the saved next-turn selection.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchApprovalModeNowCommand))]
    public partial bool IsApprovalModeChangePending { get; private set; }

    /// <summary>Gets whether full access needs a warning during this application launch.</summary>
    [ObservableProperty]
    public partial bool IsFullAccessApprovalWarningVisible { get; private set; }

    private GenerationContext? CurrentApprovalGeneration =>
        ChatContextManager.Current.GenerationContext is { IsConversationTurn: true } generation ? generation : null;

    // ChatWindowViewModel is a singleton. Dismissal survives chat switching and resets on application restart.
    private bool _hasDismissedFullAccessWarning;

    private void InitializeToolApproval()
    {
        LifetimeDisposables.Add(
            ChatContextManager.WhenValueChanged(manager => manager.Current)
                .Select(context =>
                    context is null ? Observable.Return<GenerationContext?>(null) : context.WhenValueChanged(chat => chat.GenerationContext))
                .Switch()
                .Select(generation => generation is { IsConversationTurn: true } ?
                    generation.ApprovalState.WhenValueChanged(state => state.Mode) :
                    Observable.Return(Settings.Plugin.ApprovalMode))
                .Switch()
                .Merge(Settings.Plugin.WhenValueChanged(settings => settings.ApprovalMode))
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(_ => UpdateApprovalModeState())
        );
        UpdateApprovalModeState();
    }

    private void UpdateApprovalModeState()
    {
        var selected = SelectedApprovalOption;
        var effective = EffectiveApprovalOption;
        OnPropertyChanged(nameof(SelectedApprovalOption));
        OnPropertyChanged(nameof(EffectiveApprovalOption));
        IsApprovalModeChangePending = CurrentApprovalGeneration is not null && effective != selected;
        PendingApprovalMessageKey = IsApprovalModeChangePending ?
            new FormattedDynamicLocaleKey(LocaleKey.ToolApproval_PendingChange, effective.NameKey, selected.NameKey) :
            DirectLocaleKey.Empty;
        IsFullAccessApprovalWarningVisible = !_hasDismissedFullAccessWarning && (effective.IsDangerous || selected.IsDangerous);
    }

    private ToolApprovalModeOption FindApprovalOption(ToolApprovalMode mode) =>
        ApprovalModeOptions.FirstOrDefault(option => option.Mode == mode) ?? ApprovalModeOptions[0];

    [RelayCommand]
    private async Task SelectApprovalModeAsync(ToolApprovalModeOption option)
    {
        var mode = option.Mode;
        var needsConfirmation = mode == ToolApprovalMode.Auto && !PersistentState.HasAcknowledgedAutoApproval ||
            mode == ToolApprovalMode.FullAccess && !PersistentState.HasAcknowledgedFullAccess;
        if (needsConfirmation)
        {
            var isFullAccess = mode == ToolApprovalMode.FullAccess;
            var result = await DialogHost.CreateDialog(
                    new ToolApprovalIntroduction(mode),
                    (isFullAccess ? LocaleKey.ToolApproval_FullAccessDialog_Title : LocaleKey.ToolApproval_AutoDialog_Title).I18N())
                .WithPrimaryButton(
                    (isFullAccess ? LocaleKey.ToolApproval_FullAccessDialog_Enable : LocaleKey.ToolApproval_AutoDialog_Enable).I18N(),
                    buttonStyle: isFullAccess ? ButtonStyle.Destructive : ButtonStyle.Primary)
                .WithCancelButton(LocaleKey.Common_Cancel.I18N()).ShowAsync();
            if (result != DialogResult.Primary) return;
            if (isFullAccess) PersistentState.HasAcknowledgedFullAccess = true;
            else PersistentState.HasAcknowledgedAutoApproval = true;
        }
        Settings.Plugin.ApprovalMode = mode;
    }

    [RelayCommand(CanExecute = nameof(IsApprovalModeChangePending))]
    private void SwitchApprovalModeNow()
    {
        // Resolve the current generation at execution time; never switch a previously displayed chat.
        if (CurrentApprovalGeneration is not { } generation) return;
        generation.ApprovalState.Mode = Settings.Plugin.ApprovalMode;
        UpdateApprovalModeState();
    }

    [RelayCommand]
    private void DismissFullAccessWarning()
    {
        _hasDismissedFullAccessWarning = true;
        UpdateApprovalModeState();
    }
}