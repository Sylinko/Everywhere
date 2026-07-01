using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat;

/// <summary>
/// A frame that contains all context duration one function calling
/// </summary>
public sealed record FunctionCallContext(
    Kernel Kernel,
    ChatContext ChatContext,
    ChatPlugin ChatPlugin,
    ChatFunction ChatFunction,
    FunctionCallChatMessage FunctionCallChatMessage,
    IDictionary<string, bool> IsPermissionGrantedRecords
) : IChatPluginUserInterface
{
    public string PermissionKey => $"{ChatPlugin.Key}.{ChatFunction.KernelFunction.Name}";

    public bool IsPermissionGranted
    {
        get
        {
            var permissionKey = PermissionKey;

            // If the function requires permissions that are less than FileAccess, we consider it as low-risk and grant permission by default.
            if (ChatFunction.Permissions <= ChatFunctionPermissions.AutoGranted &&
                (!ChatContext.IsPermissionGrantedRecords.ContainsKey(permissionKey) || ChatContext.IsPermissionGrantedRecords[permissionKey]))
            {
                return true;
            }

            ChatContext.IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isSessionGranted);
            return isSessionGranted;
        }
    }

    #region IChatPluginUserInterface implementation

    public IChatPluginDisplaySink DisplaySink => FunctionCallChatMessage.DisplaySink;

    public async Task<RequestConsentResult> RequestConsentAsync(
        string? id,
        IDynamicLocaleKey headerKey,
        ChatPluginDisplayBlock? content = null,
        RequestConsentRememberMasks rememberMasks = RequestConsentRememberMasks.All,
        CancellationToken cancellationToken = default)
    {
        if (id.IsNullOrEmpty() && ChatFunction.AutoApprove) return RequestConsentResult.Accepted;

        var permissionKey = id.IsNullOrEmpty() ? PermissionKey : $"{PermissionKey}.{id}";
        IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isGloballyGranted);
        ChatContext.IsPermissionGrantedRecords.TryGetValue(permissionKey, out var isSessionGranted);
        if (isGloballyGranted || isSessionGranted)
        {
            return RequestConsentResult.Accepted;
        }

        var consentDecision = await ChatContext.UserInterfaceBroker.HandleConsentRequestAsync(
            headerKey,
            content,
            rememberMasks,
            cancellationToken);

        switch (consentDecision.Decision)
        {
            case ConsentDecision.AlwaysAllow:
            {
                IsPermissionGrantedRecords[permissionKey] = true;
                return RequestConsentResult.Accepted;
            }
            case ConsentDecision.AllowSession:
            {
                ChatContext.IsPermissionGrantedRecords[permissionKey] = true;
                return RequestConsentResult.Accepted;
            }
            case ConsentDecision.AllowOnce:
            {
                return RequestConsentResult.Accepted;
            }
            case ConsentDecision.Deny:
            default:
            {
                return RequestConsentResult.Denied(consentDecision.Reason);
            }
        }
    }

    public Task<IReadOnlyList<ChatPluginQuestionAnswer>> AskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        return ChatContext.UserInterfaceBroker.HandleAskQuestionAsync(questions, cancellationToken);
    }

    #endregion
}