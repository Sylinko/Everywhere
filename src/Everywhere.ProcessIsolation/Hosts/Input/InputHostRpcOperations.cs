namespace Everywhere.ProcessIsolation.Hosts.Input;

/// <summary>Stable operation IDs for Main-to-Input-Host desired-state requests.</summary>
public static class InputHostRpcOperations
{
    /// <summary>Contract number reserved for Input Host requests.</summary>
    public const ushort ContractId = 0x0100;

    /// <summary>Method number for replacing the complete desired Input state.</summary>
    public const ushort ApplyStateMethodId = 1;

    /// <summary>Combined operation ID for <c>ApplyState</c>.</summary>
    public const uint ApplyState = (ContractId << 16) | ApplyStateMethodId;
}

/// <summary>Stable operation IDs for Input-Host-to-Main notifications.</summary>
public static class InputHostNotificationRpcOperations
{
    /// <summary>Contract number reserved for Input Host notifications.</summary>
    public const ushort ContractId = 0x0101;

    /// <summary>Method number for a triggered registered shortcut.</summary>
    public const ushort ShortcutTriggeredMethodId = 1;

    /// <summary>Method number for an in-progress capture update.</summary>
    public const ushort CaptureChangedMethodId = 2;

    /// <summary>Method number for terminal capture completion.</summary>
    public const ushort CaptureFinishedMethodId = 3;

    /// <summary>Combined operation ID for <c>ShortcutTriggered</c>.</summary>
    public const uint ShortcutTriggered = (ContractId << 16) | ShortcutTriggeredMethodId;

    /// <summary>Combined operation ID for <c>CaptureChanged</c>.</summary>
    public const uint CaptureChanged = (ContractId << 16) | CaptureChangedMethodId;

    /// <summary>Combined operation ID for <c>CaptureFinished</c>.</summary>
    public const uint CaptureFinished = (ContractId << 16) | CaptureFinishedMethodId;
}