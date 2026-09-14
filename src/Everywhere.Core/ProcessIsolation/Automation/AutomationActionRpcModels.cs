using System.Diagnostics.CodeAnalysis;
using Avalonia.Input;
using MessagePack;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Identifies one action accepted by the Automation Host.</summary>
public enum AutomationActionKind
{
    Invoke,
    SetText,
    SendKey,
    Wait,
}

/// <summary>Describes one action in an already-authorized ordered batch.</summary>
[MessagePackObject(AllowPrivate = true)]
public sealed partial class AutomationActionStep
{
    /// <summary>Action to execute.</summary>
    [Key(0)]
    public required AutomationActionKind Kind { get; init; }

    /// <summary>Positive Agent target ID for element actions.</summary>
    [Key(1)]
    public required int? TargetId { get; init; }

    /// <summary>Replacement text for <see cref="AutomationActionKind.SetText" />.</summary>
    [Key(2)]
    public required string? Text { get; init; }

    /// <summary>Key sent for <see cref="AutomationActionKind.SendKey" />.</summary>
    [Key(3)]
    public required Key Key { get; init; }

    /// <summary>Modifiers sent for <see cref="AutomationActionKind.SendKey" />.</summary>
    [Key(4)]
    public required KeyModifiers KeyModifiers { get; init; }

    /// <summary>Delay for <see cref="AutomationActionKind.Wait" />.</summary>
    [Key(5)]
    public required int? DelayMilliseconds { get; init; }

    [SerializationConstructor]
    private AutomationActionStep() { }

    [SetsRequiredMembers]
    public AutomationActionStep(
        AutomationActionKind kind,
        int? targetId = null,
        string? text = null,
        Key key = Key.None,
        KeyModifiers keyModifiers = KeyModifiers.None,
        int? delayMilliseconds = null)
    {
        Kind = kind;
        TargetId = targetId;
        Text = text;
        Key = key;
        KeyModifiers = keyModifiers;
        DelayMilliseconds = delayMilliseconds;
    }
}

/// <summary>Executes an ordered action batch in one Host-owned visual Context.</summary>
[MessagePackObject]
public sealed partial class ExecuteAutomationActionsRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Already-authorized actions in execution order.</summary>
    [Key(1)]
    public required AutomationActionStep[] Actions { get; init; }
}