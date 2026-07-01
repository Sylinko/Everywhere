using Everywhere.Interop;

namespace Everywhere.Messages;

/// <summary>
/// Raise this message to request the chat window to activate and optionally focus on a specific element within the chat window.
/// </summary>
/// <param name="TargetElement"></param>
public sealed record ActivateChatSessionMessage(IVisualElement? TargetElement = null);