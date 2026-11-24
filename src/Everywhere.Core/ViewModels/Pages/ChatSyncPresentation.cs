using Everywhere.Cloud;
using Everywhere.Utilities;
using ShadUI;

namespace Everywhere.ViewModels;

/// <summary>
/// Localized labels shared by chat sync settings and the history window.
/// </summary>
internal static class ChatSyncPresentation
{
    /// <summary>
    /// Returns a shared quota notice for actual overage or an upload that does not fit.
    /// </summary>
    public static IDynamicLocaleKey? QuotaWarning(ChatSyncStorageUsage? usage, bool hasBlockedChats)
    {
        if (usage is null || usage.UsedBytes <= usage.LimitBytes)
        {
            return hasBlockedChats ? new DynamicLocaleKey(LocaleKey.ChatSync_UploadsBlocked) : null;
        }

        var excess = Humanizer.HumanizeBytes(usage.UsedBytes - usage.LimitBytes);
        return new FormattedDynamicLocaleKey(LocaleKey.ChatSync_StorageOverLimit, new DirectLocaleKey(excess));
    }

    /// <summary>
    /// Shows confirmed stop counts and any remaining work requiring user review.
    /// </summary>
    public static void ShowReleaseResult(ToastHost host, ChatSyncReleaseResult result)
    {
        if (result.NeedsReview)
        {
            var message = LocaleKey.ChatSync_ActionUnconfirmed.I18N();
            if (result.StoppedCount > 0)
            {
                message = string.Format(LocaleKey.ChatSyncPage_ReleaseSpaceToast_Result.I18N(), result.StoppedCount) + Environment.NewLine + message;
            }
            host.CreateToast(message).ShowWarning();
        }
        else if (result.StoppedCount > 0)
        {
            host.CreateToast(string.Format(LocaleKey.ChatSyncPage_ReleaseSpaceToast_Result.I18N(), result.StoppedCount)).ShowSuccess();
        }
    }

    /// <summary>
    /// Preserves literal user titles and a dynamically localized fallback for untitled chats.
    /// </summary>
    public static IDynamicLocaleKey Title(ChatSyncItem item)
    {
        return string.IsNullOrWhiteSpace(item.Title) ?
            new DynamicLocaleKey(LocaleKey.ChatContext_Metadata_Topic_Default) :
            new DirectLocaleKey(item.Title);
    }

    /// <summary>
    /// Maps a typed sync failure to localized UI text, using the supplied fallback for other errors.
    /// </summary>
    public static DynamicLocaleKey DescribeError(Exception exception, string fallback)
    {
        return exception is ChatSyncException sync ? DescribeFailure(sync.Failure, fallback) : new DynamicLocaleKey(fallback);
    }

    /// <summary>
    /// Formats quota shortfalls in binary byte units and maps known failure codes without displaying wire diagnostics.
    /// </summary>
    public static DynamicLocaleKey DescribeFailure(ChatSyncFailure? failure, string fallback)
    {
        if (failure?.Quota?.Usage is { LimitBytes: 0 })
        {
            return new DynamicLocaleKey(LocaleKey.ChatSync_NoCloudStorage);
        }
        if (failure?.Quota is { Usage: { } usage } quota)
        {
            var needed = Humanizer.HumanizeBytes(quota.Shortfall(usage));
            return new FormattedDynamicLocaleKey(LocaleKey.ChatSync_SpaceRequired, needed);
        }

        return new DynamicLocaleKey(
            failure?.Code switch
            {
                ChatSyncFailureCode.AccountMismatch => LocaleKey.ChatSync_AccountChanged,
                ChatSyncFailureCode.SyncDisabled => LocaleKey.ChatSync_Disabled,
                ChatSyncFailureCode.MigrationRequired => LocaleKey.ChatSync_LoadFailed,
                ChatSyncFailureCode.RevisionConflict or ChatSyncFailureCode.GenerationMismatch or
                    ChatSyncFailureCode.LifecycleConflict or ChatSyncFailureCode.ContentUnavailable or
                    ChatSyncFailureCode.ChatDeleted => LocaleKey.ChatSync_CopyChanged,
                ChatSyncFailureCode.PayloadConflict => LocaleKey.ChatSync_PayloadConflict,
                _ => fallback,
            });
    }
}