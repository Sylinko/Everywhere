using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Renders FunctionCallContent to a user-friendly display block for UI presentation.
/// </summary>
public interface IFriendlyFunctionCallContentRenderer
{
    /// <summary>
    /// Renders the function call content to a user-friendly format for UI display (FriendlyFunctionCallContentPresenter).
    /// </summary>
    /// <remarks>
    /// This method will only be called when the FunctionCallContent.Arguments has at least one argument.
    /// </remarks>
    /// <param name="arguments"></param>
    /// <returns></returns>
    ChatPluginDisplayBlock? Render(KernelArguments arguments);
}

[AttributeUsage(AttributeTargets.Method)]
public class FriendlyFunctionCallContentRendererAttribute(Type rendererType) : Attribute
{
    public Type RendererType { get; } = rendererType;
}