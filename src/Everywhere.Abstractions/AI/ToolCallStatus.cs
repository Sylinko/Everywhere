namespace Everywhere.AI;

public enum ToolCallStatus
{
    Enabled,
    Disabled,

    /// <summary>
    /// The current Assistant doesn't support Tool calling
    /// </summary>
    NotSupported
}