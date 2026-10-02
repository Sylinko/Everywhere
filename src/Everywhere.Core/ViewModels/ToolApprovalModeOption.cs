using Everywhere.Chat.Permissions;
using Lucide.Avalonia;

namespace Everywhere.ViewModels;

/// <summary>Describes one fixed approval option for the selector and current-mode indicator.</summary>
public sealed record ToolApprovalModeOption(ToolApprovalMode Mode, IDynamicLocaleKey NameKey, IDynamicLocaleKey DescriptionKey, LucideIconKind Icon)
{
    /// <summary>Gets whether the option should use warning styling.</summary>
    public bool IsDangerous => Mode == ToolApprovalMode.FullAccess;
}