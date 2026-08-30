using Everywhere.Interop;

using Everywhere.Automation;

namespace Everywhere.Messages;

/// <summary>
/// Raise this message to request the chat window to activate and optionally focus on a specific element within the chat window.
/// </summary>
/// <param name="TargetLocator">The best-effort source locator, or <see langword="null" /> to activate without a visual target.</param>
/// <param name="TargetResolution">The topological result resolved from <paramref name="TargetLocator" />.</param>
public sealed record ActivateChatSessionMessage(VisualElementLocator? TargetLocator = null, VisualElementResolution TargetResolution = VisualElementResolution.Direct);
