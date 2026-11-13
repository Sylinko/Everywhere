using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Cloud;
using Everywhere.Utilities;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;

namespace Everywhere.ViewModels;

/// <summary>
/// Projects the current session onto stable rows. Owns only page selection, ordering,
/// confirmations and UI waits; it performs no inventory HTTP reads or sync scheduling.
/// </summary>
public sealed partial class ChatSyncPageViewModel(
    IChatSyncService sync,
    ICloudClient cloudClient,
    ILogger<ChatSyncPageViewModel> logger
) : ReactiveViewModelBase
{
    /// <summary>
    /// Fixed display-order choices, rendered separately in the selection box and popup.
    /// </summary>
    public static IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(LucideIconKind.Clock, new DynamicLocaleKey(LocaleKey.ChatSyncPage_RecentSortComboBoxItem_Content)),
        new(LucideIconKind.Cloud, new DynamicLocaleKey(LocaleKey.ChatSyncPage_CloudSizeSortComboBoxItem_Content))
    ];

    /// <summary>
    /// Stable rows in the selected participation scope and display order.
    /// </summary>
    public ObservableCollection<ChatSyncListRow> Rows { get; } = [];

    /// <summary>
    /// Shared account preference and current synchronization owner.
    /// </summary>
    public IChatSyncService ChatSync { get; } = sync;

    /// <summary>
    /// Authentication source used by the existing signed-out account card.
    /// </summary>
    public ICloudClient CloudClient { get; } = cloudClient;

    /// <summary>
    /// Zero selects participating chats; one selects retained local-only history.
    /// </summary>
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    /// <summary>
    /// Zero orders by newest modification, one by descending cloud bytes.
    /// Cloud-size sorting is available only for the participating scope.
    /// </summary>
    [ObservableProperty]
    public partial int SelectedSortIndex { get; set; }

    /// <summary>
    /// Page-local selection mode, independent of background synchronization.
    /// </summary>
    [ObservableProperty]
    public partial bool IsMultiSelectEnabled { get; private set; }

    /// <summary>
    /// An initial inventory exists, including a successfully loaded empty account.
    /// </summary>
    public bool HasInventory => _snapshot?.Inventory is not null;

    /// <summary>
    /// Initial content is missing while an enabled account is preparing it.
    /// </summary>
    public bool IsInventoryLoading => IsChatSyncConfigured && ChatSync.IsEnabled && (_snapshot is null || _snapshot.IsPreparing);

    /// <summary>
    /// Whether this build supplies a cloud address, independently of the stored device preference.
    /// </summary>
    public bool IsChatSyncConfigured => !CloudConstants.CloudBaseUrl.IsNullOrEmpty();

    /// <summary>
    /// Background/explicit synchronization progress; does not disable row commands.
    /// </summary>
    public bool IsSynchronizing => _snapshot?.IsSynchronizing == true;

    /// <summary>
    /// One explicit refresh may join the enabled session.
    /// </summary>
    public bool CanRefresh => _session is not null && !IsSynchronizing;

    /// <summary>
    /// Participating scope supports cloud-size sorting and multi-select.
    /// </summary>
    public bool IsParticipatingTab => SelectedTabIndex == 0;

    /// <summary>
    /// The participating scope contains rows and selection has not started.
    /// </summary>
    public bool CanStartMultiSelect => IsParticipatingTab && !IsMultiSelectEnabled && Rows.Count > 0;

    /// <summary>
    /// At least one participating row is checked for a batch command.
    /// </summary>
    public bool HasSelectedChats => Rows.AsValueEnumerable().Any(row => row is { IsChecked: true, Item.IsBusy: false });

    /// <summary>
    /// Initial unavailability or failure, not a second loading line over loaded content.
    /// </summary>
    public IDynamicLocaleKey InventoryMessageKey
    {
        get
        {
            if (!IsChatSyncConfigured)
            {
                return new DynamicLocaleKey(LocaleKey.Common_CloudNotConfigured);
            }

            return _snapshot?.Error is { } error ?
                ChatSyncPresentation.DescribeError(error, LocaleKey.ChatSync_LoadFailed) :
                new DynamicLocaleKey(ChatSync.IsEnabled ? LocaleKey.ChatSync_Loading : LocaleKey.ChatSync_Disabled);
        }
    }

    /// <summary>
    /// Meter fill in the range zero to one; actual byte counts are not clamped.
    /// </summary>
    public double StorageUsageFraction => _snapshot?.Inventory?.Usage switch
    {
        { LimitBytes: > 0 } usage => Math.Min(1d, (double)usage.UsedBytes / usage.LimitBytes),
        { UsedBytes: > 0 } => 1d,
        _ => 0d
    };

    /// <summary>
    /// Logical used/allowed cloud bytes formatted as KiB, MiB or GiB.
    /// </summary>
    public IDynamicLocaleKey StorageUsageLabelKey => new FormattedDynamicLocaleKey(
        LocaleKey.ChatSyncPage_StorageUsageTextBlock_Text,
        Humanizer.HumanizeBytes(_snapshot?.Inventory?.Usage.UsedBytes ?? 0),
        Humanizer.HumanizeBytes(_snapshot?.Inventory?.Usage.LimitBytes ?? 0));

    /// <summary>
    /// Tab caption counting participating chats independently of their transfer progress.
    /// </summary>
    public IDynamicLocaleKey ParticipatingTabLabelKey => new FormattedDynamicLocaleKey(
        LocaleKey.ChatSyncPage_ParticipatingTabItem_Header,
        _rowsById.Values.Count(row => row.IsParticipating));

    /// <summary>
    /// Tab caption counting retained local-only rows.
    /// </summary>
    public IDynamicLocaleKey LocalOnlyTabLabelKey => new FormattedDynamicLocaleKey(
        LocaleKey.ChatSyncPage_LocalOnlyTabItem_Header,
        _rowsById.Values.Count(row => !row.IsParticipating));

    /// <summary>
    /// Explicit batch action caption using the current checked count.
    /// </summary>
    public IDynamicLocaleKey StopSelectedLabelKey => new FormattedDynamicLocaleKey(
        LocaleKey.ChatSyncPage_StopSelectedButton_Content,
        Rows.Count(row => row.IsChecked));

    /// <summary>
    /// Empty-state text for the selected scope.
    /// </summary>
    public IDynamicLocaleKey EmptyListMessageKey => new DynamicLocaleKey(
        IsParticipatingTab ?
            LocaleKey.ChatSyncPage_NoParticipatingChatsTextBlock_Text :
            LocaleKey.ChatSyncPage_NoLocalOnlyChatsTextBlock_Text);

    /// <summary>
    /// Account overage or an upload that cannot fit even when account usage is below quota.
    /// </summary>
    public IDynamicLocaleKey QuotaWarningKey =>
        ChatSyncPresentation.QuotaWarning(_snapshot?.Inventory?.Usage, _snapshot?.HasQuotaBlockedChats == true) ?? DirectLocaleKey.Empty;

    private readonly Dictionary<Guid, ChatSyncListRow> _rowsById = [];
    private ChatSyncSession? _session;
    private ChatSyncSessionState? _snapshot;
    private CancellationToken _viewToken;

    protected internal override Task ViewLoaded(CancellationToken cancellationToken)
    {
        _viewToken = cancellationToken;
        ChatSync.Changed += HandleStateChanged;
        ReadState();
        return Task.CompletedTask;
    }

    protected internal override Task ViewUnloaded()
    {
        ChatSync.Changed -= HandleStateChanged;
        _session = null;
        _snapshot = null;
        _rowsById.Clear();
        Rows.Clear();
        return Task.CompletedTask;
    }

    private void HandleStateChanged(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (!_viewToken.IsCancellationRequested)
            {
                ReadState();
            }
        });
    }

    private void ReadState()
    {
        var session = ChatSync.Session;
        if (!ReferenceEquals(session, _session))
        {
            CancelSelection();
            _rowsById.Clear();
            Rows.Clear();
            _session = session;
            SelectedTabIndex = 0;
        }

        _snapshot = session?.State;
        var inventory = _snapshot?.Inventory;
        var remaining = _rowsById.Keys.ToHashSet();
        if (inventory is not null)
        {
            foreach (var item in inventory.Items)
            {
                remaining.Remove(item.Id);
                if (_rowsById.TryGetValue(item.Id, out var row))
                {
                    row.Update(item, inventory.Usage);
                }
                else
                {
                    row = new ChatSyncListRow(item, inventory.Usage, SelectionChanged);
                    _rowsById.Add(item.Id, row);
                }
                if (!row.IsParticipating)
                {
                    row.IsChecked = false;
                }
            }
        }

        foreach (var id in remaining)
        {
            _rowsById.Remove(id);
        }

        UpdateScope();
        OnPropertyChanged(string.Empty);
    }

    private void UpdateScope()
    {
        var isParticipating = IsParticipatingTab;
        var candidates = _rowsById.Values.Where(row => row.IsParticipating == isParticipating);
        var ordered = isParticipating && SelectedSortIndex == 1 ?
            candidates
                .OrderByDescending(row => row.Item.Copy?.Bytes)
                .ThenByDescending(row => row.Item.UpdatedAt)
                .ThenByDescending(row => row.Item.Id) :
            candidates
                .OrderByDescending(row => row.Item.UpdatedAt)
                .ThenByDescending(row => row.Item.Id);
        var index = 0;
        foreach (var row in ordered)
        {
            // The usual refresh changes statuses, not order; avoid scanning unchanged rows.
            if (index < Rows.Count && ReferenceEquals(Rows[index], row))
            {
                index++;
                continue;
            }

            var previous = Rows.IndexOf(row);
            if (previous < 0)
            {
                Rows.Insert(index, row);
            }
            else if (previous != index)
            {
                Rows.Move(previous, index);
            }

            index++;
        }

        while (Rows.Count > index)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        SelectionChanged();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        CancelSelection();
        UpdateScope();
        OnPropertyChanged(string.Empty);
    }

    partial void OnSelectedSortIndexChanged(int value) => UpdateScope();

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedChats));
        OnPropertyChanged(nameof(StopSelectedLabelKey));
    }

    [RelayCommand]
    private void SelectChats()
    {
        IsMultiSelectEnabled = true;
        OnPropertyChanged(nameof(CanStartMultiSelect));
    }

    [RelayCommand]
    private void CancelSelection()
    {
        IsMultiSelectEnabled = false;
        foreach (var row in _rowsById.Values)
        {
            row.IsChecked = false;
        }

        OnPropertyChanged(nameof(CanStartMultiSelect));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            // Page navigation cancels only the wait, not the account's background pass.
            await session.SynchronizeAsync(cancellationToken: _viewToken);
        }
        catch (OperationCanceledException) when (_viewToken.IsCancellationRequested || session.Token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!_viewToken.IsCancellationRequested && !session.Token.IsCancellationRequested)
            {
                ShowActionError(error);
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartAsync(ChatSyncListRow row)
    {
        if (_session is not { } session || _snapshot?.Inventory is not { } inventory || row.Item.IsBusy || !row.Item.CanStart)
        {
            return;
        }

        var item = row.Item;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_viewToken, session.Token);
        try
        {
            if (await ChatSyncDialogs.ConfirmStartAsync(DialogHost, item, cancellation.Token))
            {
                await session.StartAsync(inventory.DecisionFor(item), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                ShowActionError(error);
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task StopAsync(ChatSyncListRow row) => StopRowsAsync([row]);

    [RelayCommand]
    private Task StopSelectedAsync()
    {
        return StopRowsAsync(Rows.AsValueEnumerable().Where(row => row is { IsChecked: true, Item.IsBusy: false }).ToArray());
    }

    private async Task StopRowsAsync(ChatSyncListRow[] rows)
    {
        if (_session is not { } session || _snapshot?.Inventory is not { } inventory || rows.Length == 0)
        {
            return;
        }

        var decisions = rows
            .AsValueEnumerable()
            .Where(row => row is { IsParticipating: true, Item.IsBusy: false })
            .Select(row => inventory.DecisionFor(row.Item))
            .ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_viewToken, session.Token);
        var stopped = 0;
        try
        {
            if (decisions.Length == 0 || !await ChatSyncDialogs.ConfirmStopAsync(
                    DialogHost,
                    decisions.AsValueEnumerable().Select(decision => decision.Item).ToArray(),
                    cancellation.Token))
            {
                return;
            }

            Exception? lastError = null;
            foreach (var decision in decisions)
            {
                try
                {
                    await session.StopAsync(decision, Guid.NewGuid(), cancellation.Token);
                    stopped++;
                }
                catch (Exception error) when (error is not OperationCanceledException && !cancellation.IsCancellationRequested)
                {
                    lastError = error;
                }
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (stopped == decisions.Length)
            {
                CancelSelection();
            }
            else if (stopped > 0)
            {
                var message = string.Format(LocaleKey.ChatSyncPage_StopSelectedToast_PartialResult.I18N(), stopped, decisions.Length);
                ToastHost.CreateToast(message).ShowWarning();
            }
            else if (lastError is not null)
            {
                ShowActionError(lastError);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                ShowActionError(error);
            }
        }
    }

    [RelayCommand]
    private async Task ResolveAsync()
    {
        if (_session is not { } session || _snapshot?.Inventory is not { } inventory)
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_viewToken, session.Token);
        try
        {
            var plan = ChatSyncSpacePlan.FromInventory(inventory);
            if (await ChatSyncDialogs.ConfirmSpacePlanAsync(DialogHost, inventory, plan, cancellation.Token))
            {
                var result = await session.ReleaseSpaceAsync(inventory, plan, cancellation.Token);
                ChatSyncPresentation.ShowReleaseResult(ToastHost, result);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                ShowActionError(error);
            }
        }
    }

    private void ShowActionError(Exception error)
    {
        logger.LogWarning(error, "Chat sync management action could not be confirmed.");
        var message = ChatSyncPresentation.DescribeError(error, LocaleKey.ChatSync_ActionUnconfirmed);
        ToastHost.CreateToast(message.ToString() ?? LocaleKey.ChatSync_ActionUnconfirmed.I18N()).ShowError();
    }

    /// <summary>
    /// Localized label and icon for one fixed sort choice.
    /// </summary>
    public sealed record SortOption(LucideIconKind Icon, IDynamicLocaleKey LabelKey);
}