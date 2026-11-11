using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Everywhere.Cloud;
using Everywhere.Utilities;
using ShadUI;

namespace Everywhere.Views;

/// <summary>Shared confirmations for the history menu and the sync settings page.</summary>
internal static class ChatSyncDialogs
{
    /// <summary>
    /// Confirms account-wide cloud-copy removal while distinguishing local retention from unrecoverable remote-only copies.
    /// </summary>
    public static async Task<bool> ConfirmStopAsync(DialogHost host, IReadOnlyList<ChatSyncItem> items, CancellationToken token = default)
    {
        var target = items.Count == 1 ?
            ChatSyncPresentation.Title(items[0]).ToString() :
            string.Format(LocaleKey.ChatSyncDialogs_StopSyncDialog_SelectedCount.I18N(), items.Count);
        var message = new StringBuilder(
            items.AsValueEnumerable().Any(item => item.Copy is not null) ?
                string.Format(
                    LocaleKey.ChatSyncDialogs_StopSyncDialog_StopCloudCopies.I18N(),
                    target,
                    Humanizer.HumanizeBytes(items.AsValueEnumerable().Sum(item => item.Copy?.Bytes ?? 0))) :
                string.Format(LocaleKey.ChatSyncDialogs_StopSyncDialog_CancelUpload.I18N(), target));
        if (items.AsValueEnumerable().Any(item => !item.HasLocalCopy))
        {
            message.AppendLine().AppendLine().Append(LocaleKey.ChatSyncDialogs_StopSyncDialog_RemoteOnlyWarning.I18N());
        }

        return await host
            .CreateDialog(message.ToString(), LocaleKey.Common_StopChatSync.I18N())
            .WithPrimaryButton(LocaleKey.Common_StopChatSync.I18N(), buttonStyle: ButtonStyle.Destructive)
            .WithCancelButton(LocaleKey.Common_Cancel.I18N()).ShowAsync(token) == DialogResult.Primary;
    }

    /// <summary>
    /// Confirms publishing all branches retained on this device.
    /// </summary>
    public static async Task<bool> ConfirmStartAsync(DialogHost host, ChatSyncItem item, CancellationToken token = default)
    {
        var prompt = string.Format(LocaleKey.ChatSyncDialogs_StartSyncDialog_Content.I18N(), ChatSyncPresentation.Title(item));
        return await host
            .CreateDialog(prompt, LocaleKey.Common_StartChatSync.I18N())
            .WithPrimaryButton(LocaleKey.Common_StartChatSync.I18N()).WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync(token) == DialogResult.Primary;
    }

    /// <summary>
    /// Confirms the proposed older-copy stops or explains why the available copies cannot satisfy this upload.
    /// </summary>
    public static async Task<bool> ConfirmSpacePlanAsync(
        DialogHost host,
        ChatSyncInventory inventory,
        ChatSyncSpacePlan plan,
        CancellationToken token)
    {
        if (inventory.Usage.LimitBytes == 0 || plan is { RequiredBytes: > 0, Copies.Count: 0 })
        {
            var message = LocaleKey.ChatSync_NoCloudStorage.I18N();
            if (plan.IsTargetOverLimit)
            {
                var target = inventory.Items.AsValueEnumerable().Single(item => item.Id == plan.Target);
                message = string.Format(
                    LocaleKey.ChatSyncDialogs_ReleaseSpaceDialog_ChatExceedsLimit.I18N(),
                    ChatSyncPresentation.Title(target),
                    Humanizer.HumanizeBytes(inventory.Usage.LimitBytes));
            }
            else if (inventory.Usage.LimitBytes > 0)
            {
                message = string.Format(
                    LocaleKey.ChatSyncDialogs_ReleaseSpaceDialog_InsufficientCopies.I18N(),
                    Humanizer.HumanizeBytes(plan.RequiredBytes));
            }

            await host
                .CreateDialog(message, LocaleKey.ChatSyncDialogs_ReleaseSpaceDialog_Title.I18N())
                .WithPrimaryButton(LocaleKey.Common_OK.I18N())
                .ShowAsync(token);
            return false;
        }

        return plan.Copies.Count == 0 || await ConfirmReleaseAsync(host, plan, token);
    }

    private static async Task<bool> ConfirmReleaseAsync(DialogHost host, ChatSyncSpacePlan plan, CancellationToken token)
    {
        var list = new StackPanel
        {
            Spacing = 8
        };
        foreach (var item in plan.Copies)
        {
            list.Children.Add(
                new DockPanel
                {
                    HorizontalSpacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Humanizer.HumanizeBytes(item.Copy?.Bytes ?? 0),
                            [DockPanel.DockProperty] = Dock.Right
                        },
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = ChatSyncPresentation.Title(item).ToString(),
                                    TextWrapping = TextWrapping.Wrap
                                },
                                new TextBlock
                                {
                                    Classes = { "Muted", "Caption" },
                                    Text = item.UpdatedAt?.ToLocalTime().ToString("g")
                                }
                            }
                        }
                    }
                });
        }
        var body = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 440,
            Children =
            {
                new TextBlock
                {
                    Text = string.Format(
                        LocaleKey.ChatSyncDialogs_ReleaseSpaceDialog_Content.I18N(),
                        plan.Copies.Count,
                        Humanizer.HumanizeBytes(plan.ReleasedBytes)),
                    TextWrapping = TextWrapping.Wrap
                },
                new ScrollViewer
                {
                    Content = list,
                    MaxHeight = 200
                }
            }
        };
        return await host.CreateDialog(body, LocaleKey.ChatSyncDialogs_ReleaseSpaceDialog_Title.I18N())
            .WithPrimaryButton(LocaleKey.Common_StopChatSync.I18N(), buttonStyle: ButtonStyle.Destructive)
            .WithCancelButton(LocaleKey.Common_Cancel.I18N()).ShowAsync(token) == DialogResult.Primary;
    }
}