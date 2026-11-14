using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Cloud;
using Everywhere.Utilities;
using Lucide.Avalonia;

namespace Everywhere.ViewModels;

/// <summary>
/// A stable list identity across inventory reads for the same account.
/// </summary>
public sealed partial class ChatSyncListRow : ObservableObject
{
    /// <summary>
    /// Latest immutable inventory entry; replacing it does not replace this row identity.
    /// </summary>
    public ChatSyncItem Item { get; private set; }

    /// <summary>
    /// Single projection for the icon, text, semantic tone and accessible explanation.
    /// </summary>
    public StatusPresentation Status { get; private set; }

    /// <summary>
    /// Localized fallback title or the user-provided literal chat title.
    /// </summary>
    public IDynamicLocaleKey TitleKey => ChatSyncPresentation.Title(Item);

    /// <summary>
    /// Published cloud-copy size formatted in KiB, MiB or GiB; null before publication.
    /// </summary>
    public string? CloudSizeText => Item.Copy is { } copy ? Humanizer.HumanizeBytes(copy.Bytes) : null;

    /// <summary>
    /// Clarifies that the displayed size measures the cloud copy, not local disk usage.
    /// </summary>
    public IDynamicLocaleKey CloudSizeToolTipKey => CloudSizeText is { } size ?
        new FormattedDynamicLocaleKey(LocaleKey.ChatSyncListRow_CloudSize_ToolTip, size) :
        DirectLocaleKey.Empty;

    /// <summary>
    /// Whether this chat participates in account-wide sync, independently of progress.
    /// </summary>
    public bool IsParticipating => Item.Participation == ChatSyncParticipation.Cloud;

    /// <summary>
    /// This chat is currently being processed by the device sync pass.
    /// </summary>
    public bool IsActive => Item.Progress == ChatSyncProgress.Syncing;

    /// <summary>
    /// Selection for a batch stop; local-only rows are never included.
    /// </summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    private readonly Action _selectionChanged;
    private ChatSyncStorageUsage _usage;

    /// <summary>
    /// Creates one retained row with the current quota and a UI-thread selection callback.
    /// </summary>
    public ChatSyncListRow(ChatSyncItem item, ChatSyncStorageUsage usage, Action selectionChanged)
    {
        Item = item;
        _usage = usage;
        _selectionChanged = selectionChanged;
        Status = Describe();
    }

    partial void OnIsCheckedChanged(bool value)
    {
        _selectionChanged();
    }

    /// <summary>
    /// Applies fresh inventory and quota without replacing the row or its selection.
    /// </summary>
    public void Update(ChatSyncItem next, ChatSyncStorageUsage usage)
    {
        var hasItemChanged = Item != next;
        var hasQuotaChanged = _usage != usage && next.Failure?.Code == ChatSyncFailureCode.QuotaExceeded;
        _usage = usage;
        if (!hasItemChanged && !hasQuotaChanged)
        {
            return;
        }

        Item = next;
        Status = Describe();
        OnPropertyChanged(hasItemChanged ? string.Empty : nameof(Status));
    }

    private StatusPresentation Describe()
    {
        if (Item.Operation != ChatSyncOperation.None)
        {
            return Present(
                LucideIconKind.RefreshCw,
                Item.Operation == ChatSyncOperation.Starting ? LocaleKey.ChatSyncListRow_StartingStatus_Text : LocaleKey.ChatSyncListRow_StoppingStatus_Text,
                Item.Operation == ChatSyncOperation.Starting ? LocaleKey.Common_StartChatSync : LocaleKey.Common_StopChatSync,
                Tone.Active);
        }
        if (!IsParticipating)
        {
            return Item.CanStart ?
                Present(
                    LucideIconKind.HardDrive,
                    LocaleKey.ChatSyncListRow_LocalOnlyStatus_Text,
                    LocaleKey.ChatSyncListRow_LocalOnlyStatus_ToolTip,
                    hasSubtitle: false) :
                Present(
                    LucideIconKind.HardDrive,
                    LocaleKey.ChatSyncListRow_UnconfirmedStatus_Text,
                    LocaleKey.ChatSyncListRow_UnconfirmedStatus_ToolTip);
        }

        // The active pass supersedes the last result, not the fact that the device lacks a copy.
        if (IsActive || Item.Progress == ChatSyncProgress.Failed)
        {
            var status = IsActive ?
                Present(
                    LucideIconKind.RefreshCw,
                    LocaleKey.ChatSyncListRow_SyncingStatus_Text,
                    LocaleKey.ChatSyncListRow_SyncingStatus_ToolTip,
                    Tone.Active) :
                DescribeFailure();
            if (!Item.HasLocalCopy)
            {
                var detail = new AggregateDynamicLocaleKey(
                    [status.ToolTipKey, new DynamicLocaleKey(LocaleKey.ChatSyncListRow_RemoteOnlyStatus_Text)],
                    Environment.NewLine);
                return status with { ToolTipKey = detail };
            }
            return status;
        }

        if (!Item.HasLocalCopy)
        {
            return Present(
                LucideIconKind.CloudDownload,
                LocaleKey.ChatSyncListRow_RemoteOnlyStatus_Text,
                LocaleKey.ChatSyncListRow_RemoteOnlyStatus_ToolTip);
        }
        if (Item.Progress == ChatSyncProgress.Synced)
        {
            return Present(
                LucideIconKind.CircleCheck,
                LocaleKey.ChatSyncListRow_SyncedStatus_Text,
                LocaleKey.ChatSyncListRow_SyncedStatus_ToolTip,
                Tone.Success);
        }
        if (Item.Copy is null && Item.LocalDecisionRevision > 0)
        {
            return Present(
                LucideIconKind.Clock,
                LocaleKey.ChatSyncListRow_RestartPendingStatus_Text,
                LocaleKey.ChatSyncListRow_RestartPendingStatus_ToolTip);
        }

        var pendingTip = Item.Copy is null ? LocaleKey.ChatSyncListRow_FirstUploadStatus_ToolTip : LocaleKey.ChatSyncListRow_PendingStatus_ToolTip;
        return Present(LucideIconKind.Clock, LocaleKey.ChatSyncListRow_PendingStatus_Text, pendingTip);
    }

    private StatusPresentation DescribeFailure()
    {
        switch (Item.Failure?.Code)
        {
            case ChatSyncFailureCode.QuotaExceeded:
                return DescribeQuota();
            case ChatSyncFailureCode.RevisionConflict:
            case ChatSyncFailureCode.LifecycleConflict:
            case ChatSyncFailureCode.GenerationMismatch:
            case ChatSyncFailureCode.ContentUnavailable:
            case ChatSyncFailureCode.ChatDeleted:
                return Present(
                    LucideIconKind.RefreshCw,
                    LocaleKey.ChatSyncListRow_CloudCopyChangedStatus_Text,
                    LocaleKey.ChatSync_CopyChanged,
                    Tone.Warning);
            case ChatSyncFailureCode.PayloadConflict:
                return Present(
                    LucideIconKind.GitFork,
                    LocaleKey.ChatSync_PayloadConflict,
                    LocaleKey.ChatSyncListRow_PayloadConflictStatus_ToolTip,
                    Tone.Error);
            case ChatSyncFailureCode.AccountMismatch:
                return Present(
                    LucideIconKind.CircleAlert,
                    LocaleKey.ChatSyncListRow_FailedStatus_Text,
                    LocaleKey.ChatSyncListRow_AccountMismatchStatus_ToolTip,
                    Tone.Error);
            case ChatSyncFailureCode.SyncDisabled:
                return Present(
                    LucideIconKind.CircleAlert,
                    LocaleKey.ChatSyncListRow_FailedStatus_Text,
                    LocaleKey.ChatSync_Disabled,
                    Tone.Error);
            case ChatSyncFailureCode.MigrationRequired:
                return Present(LucideIconKind.CircleAlert, LocaleKey.ChatSyncListRow_FailedStatus_Text, LocaleKey.ChatSync_LoadFailed, Tone.Error);
            default:
                return Present(
                    LucideIconKind.CircleAlert,
                    LocaleKey.ChatSyncListRow_FailedStatus_Text,
                    LocaleKey.ChatSyncListRow_FailedStatus_ToolTip,
                    Tone.Error);
        }
    }

    private StatusPresentation DescribeQuota()
    {
        if (_usage.LimitBytes == 0)
        {
            return Present(
                LucideIconKind.CloudAlert,
                LocaleKey.ChatSyncListRow_NoCloudStorageStatus_Text,
                LocaleKey.ChatSync_NoCloudStorage,
                Tone.Warning);
        }

        var shortfall = ChatSyncSpacePlan.GetShortfall(_usage, Item.Failure);
        if (shortfall == 0)
        {
            return Present(
                LucideIconKind.Clock,
                LocaleKey.ChatSyncListRow_PendingStatus_Text,
                LocaleKey.ChatSyncListRow_SpaceAvailableStatus_ToolTip);
        }
        if (ChatSyncSpacePlan.ExceedsLimitAlone(Item, _usage, shortfall))
        {
            return Present(
                LucideIconKind.CloudAlert,
                LocaleKey.ChatSyncListRow_ChatExceedsLimitStatus_Text,
                LocaleKey.ChatSyncListRow_ChatExceedsLimitStatus_ToolTip,
                Tone.Warning);
        }

        var size = new DirectLocaleKey(Humanizer.HumanizeBytes(shortfall));
        var label = new FormattedDynamicLocaleKey(LocaleKey.ChatSync_SpaceRequired, size);
        var tip = new FormattedDynamicLocaleKey(LocaleKey.ChatSyncListRow_QuotaExceededStatus_ToolTip, size);
        return new StatusPresentation(LucideIconKind.CloudAlert, label, tip, Tone.Warning);
    }

    private static StatusPresentation Present(
        LucideIconKind icon,
        DynamicLocaleKey label,
        string tipKey,
        Tone tone = Tone.Neutral,
        bool hasSubtitle = true)
    {
        return new StatusPresentation(icon, label, new DynamicLocaleKey(tipKey), tone, hasSubtitle);
    }

    /// <summary>
    /// Semantic emphasis mapped to theme resources by the view, not literal colors.
    /// </summary>
    public enum Tone
    {
        Neutral,
        Success,
        Active,
        Warning,
        Error
    }

    /// <summary>
    /// One decision drives the icon, subtitle, color and accessible explanation.
    /// </summary>
    /// <param name="Icon">Caption-sized icon displayed before the subtitle.</param>
    /// <param name="LabelKey">Localized status subtitle.</param>
    /// <param name="ToolTipKey">Accessible explanation and next action.</param>
    /// <param name="Color">Semantic tone, resolved to dynamic brushes by the view.</param>
    /// <param name="HasSubtitle">False for ordinary local-only rows, avoiding an empty status line.</param>
    public sealed record StatusPresentation(
        LucideIconKind Icon,
        DynamicLocaleKey LabelKey,
        IDynamicLocaleKey ToolTipKey,
        Tone Color = Tone.Neutral,
        bool HasSubtitle = true
    )
    {
        /// <summary>
        /// Uses the success tone while keeping ordinary status text muted.
        /// </summary>
        public bool IsSuccess => Color == Tone.Success;

        /// <summary>
        /// Uses the active sync accent.
        /// </summary>
        public bool IsActive => Color == Tone.Active;

        /// <summary>
        /// Uses the quota or changed-copy warning tone.
        /// </summary>
        public bool IsWarning => Color == Tone.Warning;

        /// <summary>
        /// Uses the conflict or failed-operation error tone.
        /// </summary>
        public bool IsError => Color == Tone.Error;
    }
}