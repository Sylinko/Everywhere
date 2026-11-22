using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;

namespace Everywhere.Harness;

/// <summary>
/// Exposes Harness invocation lifecycle and backgrounding controls to a running tool
/// without leaking engine-internal generation, approval, or message graph state.
/// </summary>
public interface IChatPluginExecutionContext
{
    /// <summary>
    /// Gets the stable tool-call identifier for this invocation.
    /// </summary>
    string InvocationId { get; }

    /// <summary>
    /// Gets the approval mode captured before this tool started.
    /// </summary>
    ToolApprovalMode ApprovalMode { get; }

    /// <summary>
    /// Gets whether this tool opted into background execution (<see cref="ChatFunction.SupportsBackgrounding"/>).
    /// </summary>
    bool SupportsBackgrounding { get; }

    /// <summary>
    /// Gets whether this invocation has detached from the foreground turn into a background job
    /// (via <see cref="TryMoveToBackground"/>, <see cref="BeginForegroundBudget"/>, or user steering).
    /// </summary>
    bool IsBackgrounded { get; }

    /// <summary>
    /// Immediately requests moving this invocation from foreground wait to background execution
    /// without cancelling the running task.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this call transitioned the invocation into background execution;
    /// otherwise <see langword="false"/>.
    /// </returns>
    bool TryMoveToBackground();

    /// <summary>
    /// Starts a foreground wait budget. If the invocation is still running in the foreground when
    /// <paramref name="budget"/> elapses, transitions it to background execution.
    /// </summary>
    IDisposable BeginForegroundBudget(TimeSpan budget);
}
