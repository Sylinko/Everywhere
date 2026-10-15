namespace Everywhere.Chat.Plugins;

/// <summary>
/// Inspects a call when existing approval rules have not already allowed it to bypass consent.
/// Returns true to allow this call, false to deny it, or null to continue normal approval.
/// </summary>
/// <remarks>
/// Runs synchronously on the calling thread, which is not necessarily the UI thread.
/// This hook is not a substitute for validation inside the tool because approval may be bypassed.
/// Modify the request to customize normal approval; the host owns interaction and rule persistence.
/// Request values may outlive the callback and must not retain collectible plugin implementations.
/// </remarks>
/// <param name="context">The current invocation, including its original call and snapshot identities.</param>
/// <param name="request">The default consent request, which the hook can customize for this invocation.</param>
/// <returns>An explicit decision, or null when the hook does not decide.</returns>
public delegate bool? PermissionConsentHook(FunctionCallContext context, PermissionConsentRequest request);