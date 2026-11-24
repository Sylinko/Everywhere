using Avalonia.Controls;
using Everywhere.Chat.Permissions;
using Tabler.Avalonia;

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
                    TablerIconKind.Folder,
                    LocaleKey.ToolApproval_FullAccessDialog_FilesTitle,
                    LocaleKey.ToolApproval_FullAccessDialog_FilesBody),
                Card(
                    TablerIconKind.Terminal,
                    LocaleKey.ToolApproval_FullAccessDialog_TerminalTitle,
                    LocaleKey.ToolApproval_FullAccessDialog_TerminalBody),
                Card(
                    TablerIconKind.World,
                    LocaleKey.ToolApproval_FullAccessDialog_ConnectionsTitle,
                    LocaleKey.ToolApproval_FullAccessDialog_ConnectionsBody)
            ] :
            [
                Card(
                    TablerIconKind.Robot,
                    LocaleKey.ToolApproval_AutoDialog_ReviewerTitle,
                    LocaleKey.ToolApproval_AutoDialog_ReviewerBody),
                Card(
                    TablerIconKind.FileSearch,
                    LocaleKey.ToolApproval_AutoDialog_ContextTitle,
                    LocaleKey.ToolApproval_AutoDialog_ContextBody),
                Card(
                    TablerIconKind.ShieldCheck,
                    LocaleKey.ToolApproval_AutoDialog_DecisionTitle,
                    LocaleKey.ToolApproval_AutoDialog_DecisionBody)
            ];
        InitializeComponent();
        DataContext = this;
    }

    private static ToolApprovalIntroductionCard Card(TablerIconKind icon, string title, string body) =>
        new(icon, new DynamicLocaleKey(title), new DynamicLocaleKey(body));
}

/// <summary>One localized explanation card in a first-use approval dialog.</summary>
public sealed record ToolApprovalIntroductionCard(TablerIconKind Icon, IDynamicLocaleKey TitleKey, IDynamicLocaleKey BodyKey);