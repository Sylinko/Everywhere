using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Creates optional display content before approval and tool execution, including calls without arguments.
/// </summary>
/// <remarks>
/// Runs synchronously on the calling thread, which is not necessarily the UI thread.
/// Approval may subsequently deny the call; this hook must not perform the tool's operation or grant consent.
/// Returned content can outlive the plugin and should use host-owned display types and values.
/// </remarks>
/// <param name="content">The original call, including its identity and possibly absent arguments.</param>
/// <returns>A display block to append to the conversation, or null to add no block.</returns>
public delegate ChatPluginDisplayBlock? BeforeCallHook(FunctionCallContent content);