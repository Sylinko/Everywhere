using Avalonia.Controls;
using Everywhere.Chat.Permissions;
using Lucide.Avalonia;

namespace Everywhere.Views;

/// <summary>
/// Explains the selected approval mode before its first enablement.
/// </summary>
public sealed partial class ToolApprovalIntroduction : UserControl
{
    public IDynamicLocaleKey IntroductionKey { get; }

    public IDynamicLocaleKey NoteKey { get; }

    public IReadOnlyList<ToolApprovalIntroductionCard> Cards { get; }

    public ToolApprovalIntroduction(ToolApprovalMode mode)
    {
        var isFullAccess = mode == ToolApprovalMode.FullAccess;
        IntroductionKey = new DynamicLocaleKey(
            isFullAccess ? LocaleKey.ToolApproval_FullAccessDialog_Introduction : LocaleKey.ToolApproval_AutoDialog_Introduction);
        NoteKey = new DynamicLocaleKey(
            isFullAccess ? LocaleKey.ToolApproval_FullAccessDialog_Note : LocaleKey.ToolApproval_AutoDialog_Note);
        Cards = isFullAccess ?
            [
                Card(
                    LucideIconKind.Folder,
                    LocaleKey.ToolApproval_FullAccessDialog_FilesTitle,
                    LocaleKey.ToolApproval_FullAccessDialog_FilesBody),
                Card(
                    LucideIconKind.Terminal,
                    LocaleKey.ToolApproval_FullAccessDialog_TerminalTitle,
                    LocaleKey.ToolApproval_FullAccessDialog_TerminalBody),
                Card(
                    LucideIconKind.Globe,
                    LocaleKey.ToolApproval_FullAccessDialog_ConnectionsTitle,
                    LocaleKey.ToolApproval_FullAccessDialog_ConnectionsBody)
            ] :
            [
                Card(
                    LucideIconKind.Bot,
                    LocaleKey.ToolApproval_AutoDialog_ReviewerTitle,
                    LocaleKey.ToolApproval_AutoDialog_ReviewerBody),
                Card(
                    LucideIconKind.FileSearch,
                    LocaleKey.ToolApproval_AutoDialog_ContextTitle,
                    LocaleKey.ToolApproval_AutoDialog_ContextBody),
                Card(
                    LucideIconKind.ShieldCheck,
                    LocaleKey.ToolApproval_AutoDialog_DecisionTitle,
                    LocaleKey.ToolApproval_AutoDialog_DecisionBody)
            ];
        InitializeComponent();
        DataContext = this;
    }

    private static ToolApprovalIntroductionCard Card(LucideIconKind icon, string title, string body) =>
        new(icon, new DynamicLocaleKey(title), new DynamicLocaleKey(body));
}

/// <summary>One localized explanation card in a first-use approval dialog.</summary>
public sealed record ToolApprovalIntroductionCard(LucideIconKind Icon, IDynamicLocaleKey TitleKey, IDynamicLocaleKey BodyKey);