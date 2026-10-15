using System.Reflection;
using System.Text.Json;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins.Mcp;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

public abstract class ChatFunction
{
    public virtual IDynamicLocaleKey HeaderKey => new DirectLocaleKey(KernelFunction.Name);

    public virtual IDynamicLocaleKey DescriptionKey => new DirectLocaleKey(KernelFunction.Description);

    public LucideIconKind? Icon { get; init; }

    /// <summary>
    /// The permissions required by this function.
    /// </summary>
    public virtual ChatFunctionPermissions Permissions => ChatFunctionPermissions.AllAccess;

    public bool IsDefaultEnabled { get; protected init; } = true;

    /// <summary>
    /// Gets whether calls to this function bypass approval when no user override exists.
    /// </summary>
    public bool IsDefaultBypassApproval { get; protected init; }

    /// <summary>
    /// Gets whether persistent rules may bypass approval for this function.
    /// </summary>
    public bool CanBypassApproval { get; protected init; } = true;

    public bool IsExperimental { get; protected init; }

    public bool IsVisible { get; protected init; } = true;

    public abstract KernelFunction KernelFunction { get; }

    /// <summary>
    /// Gets the optional callback that creates display content before approval and execution.
    /// </summary>
    public virtual BeforeCallHook? BeforeCallHook => null;

    /// <summary>
    /// Gets the optional consent callback. A null result continues normal approval;
    /// true allows the call and false denies it without requesting consent.
    /// </summary>
    public virtual PermissionConsentHook? PermissionConsentHook => null;
}

/// <summary>
/// A managed tool function shared by application-owned and plugin-supplied tool groups.
/// Registration supplies revocable wrappers when its implementation has a shorter lifetime than chat history.
/// </summary>
public sealed class NativeChatFunction : ChatFunction
{
    public override IDynamicLocaleKey HeaderKey { get; }

    public override IDynamicLocaleKey DescriptionKey => field ?? base.DescriptionKey;

    public override ChatFunctionPermissions Permissions { get; }

    public override KernelFunction KernelFunction { get; }

    /// <inheritdoc/>
    public override BeforeCallHook? BeforeCallHook { get; }

    /// <inheritdoc/>
    public override PermissionConsentHook? PermissionConsentHook { get; }

    /// <summary>
    /// Creates a chat tool through SK's method factory using plugin-owned converters or generated
    /// JSON metadata for schema generation and binding. Null options retain SK's default behavior.
    /// </summary>
    public NativeChatFunction(
        Delegate method,
        ChatFunctionPermissions permissions,
        LucideIconKind? icon = null,
        bool isDefaultEnabled = true,
        bool? isDefaultBypassApproval = null,
        bool canBypassApproval = true,
        bool isExperimental = false,
        bool isVisible = true,
        BeforeCallHook? beforeCallHook = null,
        PermissionConsentHook? permissionConsentHook = null,
        string? functionName = null,
        string? description = null,
        IDynamicLocaleKey? headerKey = null,
        IDynamicLocaleKey? descriptionKey = null,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (headerKey is not null)
        {
            HeaderKey = headerKey;
        }
        else if (method.Method.GetCustomAttributes<DynamicLocaleKeyAttribute>(false).FirstOrDefault() is { HeaderKey.Length: > 0 } attribute)
        {
            HeaderKey = new DynamicLocaleKey(attribute.HeaderKey);
            if (!attribute.DescriptionKey.IsNullOrWhiteSpace())
            {
                DescriptionKey = new DynamicLocaleKey(attribute.DescriptionKey);
            }
        }
        else if (!functionName.IsNullOrWhiteSpace())
        {
            HeaderKey = new DirectLocaleKey(functionName);
        }
        else if (method.Method.GetCustomAttributes<KernelFunctionAttribute>(false).FirstOrDefault() is { Name: { Length: > 0 } name })
        {
            HeaderKey = new DirectLocaleKey(name);
        }
        else
        {
            HeaderKey = new DirectLocaleKey(method.Method.Name);
        }

        if (descriptionKey is not null)
        {
            DescriptionKey = descriptionKey;
        }

        Permissions = permissions;
        Icon = icon;
        IsDefaultEnabled = isDefaultEnabled;
        IsDefaultBypassApproval = isDefaultBypassApproval ?? permissions <= ChatFunctionPermissions.BypassApproval;
        CanBypassApproval = canBypassApproval;
        IsExperimental = isExperimental;
        IsVisible = isVisible;
        BeforeCallHook = beforeCallHook;
        PermissionConsentHook = permissionConsentHook;

        var options = new KernelFunctionFromMethodOptions
        {
            FunctionName = functionName.IsNullOrWhiteSpace() ? null : functionName,
            Description = description
        };
        KernelFunction = jsonSerializerOptions is null ?
            KernelFunctionFactory.CreateFromMethod(method, options) :
            KernelFunctionFactory.CreateFromMethod(method, jsonSerializerOptions, options);
    }

    /// <summary>
    /// Copies presentation and permissions for a host-owned invocation wrapper.
    /// Callbacks supplied here resolve through the live registration rather than retaining plugin delegates.
    /// </summary>
    internal NativeChatFunction(
        KernelFunction function,
        NativeChatFunction source,
        PermissionConsentHook? permissionConsentHook,
        BeforeCallHook? beforeCallHook)
    {
        KernelFunction = function;
        HeaderKey = source.HeaderKey;
        DescriptionKey = source.DescriptionKey;
        Permissions = source.Permissions;
        Icon = source.Icon;
        IsDefaultEnabled = source.IsDefaultEnabled;
        IsDefaultBypassApproval = source.IsDefaultBypassApproval;
        CanBypassApproval = source.CanBypassApproval;
        IsExperimental = source.IsExperimental;
        IsVisible = source.IsVisible;
        PermissionConsentHook = permissionConsentHook;
        BeforeCallHook = beforeCallHook;
    }
}

public class McpChatFunction : ChatFunction
{
    public override ChatFunctionPermissions Permissions => ChatFunctionPermissions.MCP;

    public override KernelFunction KernelFunction => _kernelFunction;

    /// <inheritdoc/>
    public override PermissionConsentHook PermissionConsentHook => PermissionConsent;

    public string OriginalName { get; private set; }

    private KernelFunction _kernelFunction;

    public McpChatFunction(ManagedMcpClientTool tool)
    {
        OriginalName = tool.ProtocolTool.Name;
        _kernelFunction = tool.AsKernelFunction();
        CanBypassApproval = true;
    }

    public void Update(ManagedMcpClientTool tool)
    {
        OriginalName = tool.ProtocolTool.Name;
        _kernelFunction = tool.AsKernelFunction();
    }

    /// <summary>
    /// Prepares server-specific consent using snapshot values, without retaining the live MCP plugin.
    /// The host handles the resulting tool-group approval like any other native or remote tool.
    /// </summary>
    private static bool? PermissionConsent(FunctionCallContext context, PermissionConsentRequest request)
    {
        request.HeaderKey = new FormattedDynamicLocaleKey(LocaleKey.ChatPluginConsentRequest_MCP_Header, context.ChatFunction.HeaderKey);
        request.CustomOptions =
        [
            new RequestConsentCustomOption(
                ToolConsentOption.BypassPluginApproval,
                new FormattedDynamicLocaleKey(LocaleKey.ChatPluginConsentRequest_MCP_BypassServerApproval_Header, context.ChatPlugin.HeaderKey),
                null)
        ];
        return null;
    }
}
