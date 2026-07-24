using System.Reflection;
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
    /// Converts the function call content to a user-friendly format for UI display.
    /// </summary>
    /// <param name="content"></param>
    /// <returns></returns>
    public virtual ChatPluginDisplayBlock? GetFriendlyCallContent(FunctionCallContent content) => null;
}

public sealed class BuiltInChatFunction : ChatFunction
{
    public override IDynamicLocaleKey HeaderKey { get; }

    public override IDynamicLocaleKey DescriptionKey => field ?? base.DescriptionKey;

    public override ChatFunctionPermissions Permissions { get; }

    public override KernelFunction KernelFunction { get; }

    /// <summary>
    /// An optional predicate that can be used to inspect the function call content before prompting the user for permission consent.
    /// This will be called only if the function call requires user consent and does not bypass approval.
    /// If the predicate returns false, the function call will be **rejected** without prompting the user.
    /// If the predicate returns true, the function call will be **approved** without prompting the user.
    /// If the predicate is null or returns null, the user will be prompted for consent without additional checks (default behavior).
    /// </summary>
    public Func<FunctionCallContent, bool?>? OnPermissionConsent { get; init; }

    private readonly IFriendlyFunctionCallContentRenderer? _renderer;

    public BuiltInChatFunction(
        Delegate method,
        ChatFunctionPermissions permissions,
        LucideIconKind? icon = null,
        bool isDefaultEnabled = true,
        bool? isDefaultBypassApproval = null,
        bool canBypassApproval = true,
        bool isExperimental = false,
        bool isVisible = true,
        Func<FunctionCallContent, bool?>? onPermissionConsent = null,
        string? functionName = null,
        string? description = null,
        IDynamicLocaleKey? headerKey = null,
        IDynamicLocaleKey? descriptionKey = null)
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
        OnPermissionConsent = onPermissionConsent;

        KernelFunction = functionName.IsNullOrWhiteSpace() && description is null ?
            KernelFunctionFactory.CreateFromMethod(method) :
            KernelFunctionFactory.CreateFromMethod(
                method,
                new KernelFunctionFromMethodOptions
                {
                    FunctionName = functionName,
                    Description = description
                });

        if (method.Method.GetCustomAttributes<FriendlyFunctionCallContentRendererAttribute>(false).FirstOrDefault() is
            { RendererType: { } rendererType })
        {
            if (!typeof(IFriendlyFunctionCallContentRenderer).IsAssignableFrom(rendererType))
            {
                throw new InvalidOperationException(
                    $"The renderer type '{rendererType.FullName}' does not implement {nameof(IFriendlyFunctionCallContentRenderer)}.");
            }

            _renderer = Activator.CreateInstance(rendererType) as IFriendlyFunctionCallContentRenderer;
        }
    }

    public override ChatPluginDisplayBlock? GetFriendlyCallContent(FunctionCallContent content)
    {
        if (content.Arguments is not { Count: > 0 } arguments) return base.GetFriendlyCallContent(content);
        return _renderer?.Render(arguments) ?? base.GetFriendlyCallContent(content);
    }
}

public class McpChatFunction : ChatFunction
{
    public override ChatFunctionPermissions Permissions => ChatFunctionPermissions.MCP;

    public override KernelFunction KernelFunction => _kernelFunction;

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
}
