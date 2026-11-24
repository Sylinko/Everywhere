using System.Buffers;
using System.Reflection;
using System.Text.Json;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Everywhere.Harness;
using Everywhere.Harness.Mcp;
using Tabler.Avalonia;
using Microsoft.Extensions.AI;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Represents a tool function descriptor with localized presentation and approval metadata.
/// </summary>
public abstract class ChatFunction
{
    public virtual IDynamicLocaleKey HeaderKey => new DirectLocaleKey(Function.Name);

    public virtual IDynamicLocaleKey DescriptionKey => new DirectLocaleKey(Function.Description);

    public TablerIconKind? Icon { get; init; }

    /// <summary>
    /// Gets the permissions required by this function.
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

    /// <summary>
    /// Gets whether this function is considered experimental and shows a warning badge in the UI. Experimental functions may be removed or changed without notice.
    /// </summary>
    public bool IsExperimental { get; protected init; }

    /// <summary>
    /// Gets whether this function is visible in the UI. Hidden functions may still be invoked by name or through other tools.
    /// </summary>
    public bool IsVisible { get; protected init; } = true;

    /// <summary>
    /// Opts into running alongside adjacent opted-in invocations, including other calls to this tool.
    /// </summary>
    public bool SupportsParallelInvoke { get; protected init; }

    /// <summary>
    /// Opts into detaching an active foreground invocation into session-scoped background execution.
    /// </summary>
    public bool SupportsBackgrounding { get; protected init; }

    /// <summary>
    /// Gets the input modalities a model must support for this function to be available.
    /// </summary>
    public Modalities RequiredModalities { get; protected init; } = Modalities.None;

    /// <summary>
    /// Gets the native MEAI <see cref="AIFunction"/> backing this descriptor.
    /// </summary>
    public abstract AIFunction Function { get; }

    /// <summary>
    /// Gets the canonical function name within its owning plugin.
    /// </summary>
    public string Name => Function.Name;

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
/// Wraps a native C# method or <see cref="AIFunction"/> as an Everywhere managed tool function.
/// </summary>
public sealed class NativeChatFunction : ChatFunction
{
    /// <summary>
    /// Maximum length allowed for a canonical native function name, leaving room for wire prefixes and suffixes.
    /// </summary>
    public const int MaxNativeFunctionNameLength = 55;

    public override IDynamicLocaleKey HeaderKey { get; }

    public override IDynamicLocaleKey DescriptionKey => field ?? base.DescriptionKey;

    public override ChatFunctionPermissions Permissions { get; }

    public override AIFunction Function { get; }

    /// <inheritdoc />
    public override BeforeCallHook? BeforeCallHook { get; }

    /// <inheritdoc />
    public override PermissionConsentHook? PermissionConsentHook { get; }

    private static readonly SearchValues<char> AllowedFunctionNameChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");

    /// <summary>
    /// Registers an existing MEAI <see cref="AIFunction"/> with localized presentation and approval policy.
    /// </summary>
    public NativeChatFunction(
        AIFunction function,
        ChatFunctionPermissions permissions,
        IDynamicLocaleKey? headerKey = null,
        IDynamicLocaleKey? descriptionKey = null
    )
    {
        ValidateFunctionName(function.Name);
        Function = function;
        Permissions = permissions;
        IsDefaultBypassApproval = permissions <= ChatFunctionPermissions.BypassApproval;
        HeaderKey = headerKey ?? new DirectLocaleKey(function.Name);
        if (descriptionKey is not null)
        {
            DescriptionKey = descriptionKey;
        }
    }

    public NativeChatFunction(
        Delegate method,
        ChatFunctionPermissions permissions,
        TablerIconKind? icon = null,
        bool isDefaultEnabled = true,
        bool? isDefaultBypassApproval = null,
        bool canBypassApproval = true,
        bool isExperimental = false,
        bool isVisible = true,
        bool supportsParallelInvoke = false,
        bool supportsBackgrounding = false,
        Modalities requiredModalities = Modalities.None,
        BeforeCallHook? beforeCallHook = null,
        PermissionConsentHook? permissionConsentHook = null,
        string? functionName = null,
        string? description = null,
        IDynamicLocaleKey? headerKey = null,
        IDynamicLocaleKey? descriptionKey = null,
        JsonSerializerOptions? jsonSerializerOptions = null
    )
    {
        var attributeFunctionName = method.Method.GetCustomAttribute<ChatFunctionAttribute>(false)?.Name;
        var resolvedFunctionName = !functionName.IsNullOrWhiteSpace() ? functionName : attributeFunctionName;

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
        else if (!resolvedFunctionName.IsNullOrWhiteSpace())
        {
            HeaderKey = new DirectLocaleKey(resolvedFunctionName);
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
        SupportsParallelInvoke = supportsParallelInvoke;
        SupportsBackgrounding = supportsBackgrounding;
        RequiredModalities = requiredModalities;
        BeforeCallHook = beforeCallHook;
        PermissionConsentHook = permissionConsentHook;

        Function = CreateNativeFunction(method, resolvedFunctionName, description, jsonSerializerOptions);
        ValidateFunctionName(Function.Name);
    }

    /// <summary>
    /// Copies presentation and permissions for a host-owned invocation wrapper.
    /// Callbacks supplied here resolve through the live registration rather than retaining plugin delegates.
    /// </summary>
    internal NativeChatFunction(
        AIFunction function,
        NativeChatFunction source,
        BeforeCallHook? beforeCallHook,
        PermissionConsentHook? permissionConsentHook
    )
    {
        Function = function;
        HeaderKey = source.HeaderKey;
        DescriptionKey = source.DescriptionKey;
        Icon = source.Icon;
        Permissions = source.Permissions;
        IsDefaultEnabled = source.IsDefaultEnabled;
        IsDefaultBypassApproval = source.IsDefaultBypassApproval;
        CanBypassApproval = source.CanBypassApproval;
        IsExperimental = source.IsExperimental;
        IsVisible = source.IsVisible;
        SupportsParallelInvoke = source.SupportsParallelInvoke;
        SupportsBackgrounding = source.SupportsBackgrounding;
        RequiredModalities = source.RequiredModalities;
        BeforeCallHook = beforeCallHook;
        PermissionConsentHook = permissionConsentHook;
    }

    private static void ValidateFunctionName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name.Length > MaxNativeFunctionNameLength ||
            name.AsSpan().ContainsAnyExcept(AllowedFunctionNameChars))
        {
            throw new ArgumentException(
                $"Chat function name '{name}' must match ^[a-zA-Z0-9_-]{{1,{MaxNativeFunctionNameLength}}}$.",
                nameof(name));
        }
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> using Everywhere's service-injection and raw-result marshaling conventions.
    /// </summary>
    public static AIFunction CreateNativeFunction(
        Delegate method,
        string? name = null,
        string? description = null,
        JsonSerializerOptions? jsonSerializerOptions = null
    )
    {
        var resolvedName = !name.IsNullOrWhiteSpace()
            ? name
            : method.Method.GetCustomAttribute<ChatFunctionAttribute>(false)?.Name;

        // MEAI caches ReflectionAIFunctionDescriptor weakly by SerializerOptions; avoid pinning collectible
        // mod assemblies into the host-wide AIJsonUtilities.DefaultOptions entry when none was supplied.
        var effectiveSerializerOptions = jsonSerializerOptions ??
            (method.Method.Module.Assembly.IsCollectible ? new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) : null);

        return AIFunctionFactory.Create(
            method,
            new AIFunctionFactoryOptions
            {
                Name = resolvedName,
                Description = description,
                ExcludeResultSchema = true,
                SerializerOptions = effectiveSerializerOptions,
                ConfigureParameterBinding = ConfigureServiceParameterBinding,
                MarshalResult = static (value, _, _) => ValueTask.FromResult(value)
            });
    }

    private static AIFunctionFactoryOptions.ParameterBindingOptions ConfigureServiceParameterBinding(ParameterInfo parameter)
    {
        if (!parameter.IsDefined(typeof(FromChatServicesAttribute), inherit: false))
        {
            return default;
        }

        return new AIFunctionFactoryOptions.ParameterBindingOptions
        {
            ExcludeFromSchema = true,
            BindParameter = static (param, arguments) =>
            {
                var services = arguments.Services
                    ?? throw new InvalidOperationException($"Services are required to bind parameter '{param.Name}'.");
                var service = services.GetService(param.ParameterType);
                if (service is null && !param.HasDefaultValue)
                {
                    throw new InvalidOperationException($"No service for type '{param.ParameterType}' has been registered.");
                }

                return service ?? param.DefaultValue;
            }
        };
    }
}

/// <summary>
/// Wraps a remote MCP tool as a <see cref="ChatFunction"/>.
/// </summary>
public class McpChatFunction : ChatFunction
{
    public override ChatFunctionPermissions Permissions => ChatFunctionPermissions.MCP;

    public override ManagedMcpClientTool Function => _tool;

    /// <inheritdoc />
    public override PermissionConsentHook PermissionConsentHook => PermissionConsent;

    public string OriginalName => _tool.ProtocolTool.Name;

    private ManagedMcpClientTool _tool;

    public McpChatFunction(ManagedMcpClientTool tool)
    {
        _tool = tool;
        CanBypassApproval = true;
    }

    public void Update(ManagedMcpClientTool tool)
    {
        _tool = tool;
    }

    /// <summary>
    /// Captures the current tool without mutating the live settings UI descriptor on later discovery.
    /// </summary>
    public McpChatFunction Snapshot() => (McpChatFunction)MemberwiseClone();

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
