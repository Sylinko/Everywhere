using Microsoft.Extensions.AI;
using MonoMod;

namespace Everywhere.Patches.MicrosoftExtensionsAI.OpenAI;

/// <summary>
/// Allows Everywhere to consume Responses tool calls whose upstream response omitted
/// call_id, which MEAI rejects before the application's wrapper can inspect the call.
/// </summary>
/// <remarks>
/// Only null or empty IDs receive a new identifier. The original parser remains intact,
/// preserving valid IDs and captured argument errors. Everywhere replays full stateless
/// history, so the identifier can link the call and its result in the next request; it
/// is not a recovered identifier for a server-stored conversation.
/// See <see href="https://github.com/dotnet/extensions/blob/b10f9c0a081b5dbb7755b8f5592e1d3c3f550a3a/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIClientExtensions.cs">the pinned upstream parser</see>.
/// </remarks>
[MonoModPatch("Microsoft.Extensions.AI.OpenAIClientExtensions")]
internal static class patch_OpenAIClientExtensions
{
    // Do not use MonoModReplace: preserve the SDK's parser and its captured argument errors.
    internal static extern FunctionCallContent orig_ParseCallContent(string json, string callId, string name);

    internal static FunctionCallContent ParseCallContent(string json, string? callId, string name) =>
        orig_ParseCallContent(json, string.IsNullOrEmpty(callId) ? Guid.CreateVersion7().ToString("N") : callId, name);
}