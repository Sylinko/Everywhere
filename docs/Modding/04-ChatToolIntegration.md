# Chat Tool Integration

This document owns the chat-tool registration contract and author instructions. See the
[engine specification](01-Specification.md) for activation and cleanup, the
[developer guide](02-ModDeveloperGuide.md) for packaging, and [validation](03-Validation.md)
for evidence and remaining limits. Mod-owned settings have a separate
[contract](05-SettingsSpecification.md) and [author guide](06-SettingsDeveloperGuide.md).

**Status:** registration, revocable SK proxies and named hooks are implemented in the workspace.
This does not guarantee collection of arbitrary mod results, metadata or UI objects.

## Registration contract

NativeChatPlugin is the common base for in-process managed tools. BuiltInChatPlugin uses
`builtin.{Name}`; ModChatPlugin uses `mod.{Name}`. Both use NativeChatFunction and appear in
the manager's NativePlugins collection. MCP tools keep `mcp.{Id}` identities and their own collection.
Mod tool-group names must be stable and unique within the mod namespace. Key identifies saved
settings and approval rules; model-facing aliases and activation lifetimes must not change it.
Registration ownership, not the Key prefix, determines whether functions use revocable proxies.

`IChatPluginManager.RegisterChatPlugin(NativeChatPlugin)` returns an asynchronous reversal handle.
Override ChatPlugin.SettingsItems to display tool-specific settings in the existing chat tool page.
This does not require SettingsEngine or IModSettingsRegistry. The latter registers a separate,
optional mod-wide settings entry for a future mod page. Tools can expose different settings
from the mod-wide entry, including different sections of a shared model.
The chat page clears selected details when the tool is removed and clears the settings tab's item
source so retained view bindings release their model.
Background removal is delivered through the UI scheduler. Completing registration disposal drains
tool calls, not UI processing. An entry whose editors share its settings model must coordinate UI
detachment before final saving; ordinary process exit must not wait for a live dispatcher.
Discovery exposes the real chat-tool group, preserving its identity, settings, warnings and
`GetAvailableFunctionsAsync` behavior. Each chat scope selects available functions through the
existing enablement rules and then replaces registered functions with host-owned proxies. Snapshots
copy host-owned display values rather than retaining the ChatPlugin subclass. Previously selected
functions stay executable for the activation even if later discovery returns a different set.

The proxy separates persistent descriptions from execution state:

- Materialize parameter and return schemas through SK while the original function is alive. Retain
  names, descriptions, required flags, original DefaultValue values and the resulting schemas.
  Build new parameter metadata without ParameterType, UnderlyingMethod or serializer options.
  Assign DefaultValue directly without a type whitelist, serialization or conversion; reference-type
  defaults remain shared with the original metadata. A default that directly or indirectly references
  mod objects or types can prevent collection while the snapshot is retained. Authors own these
  references; the host does not erase defaults to force unloading, and unconfirmed collection requires
  RestartRequired. Actual argument/default binding remains SK's responsibility.
- Shallow-copy function metadata's AdditionalProperties into a new host-owned dictionary for each
  proxy, including clones. Preserve all keys and values without filtering, serialization or conversion.
  Later additions, removals or replacements in the source dictionary do not change the snapshot;
  referenced values remain shared. Mod objects, delegates, types or containers that reference them
  can keep the mod alive while a snapshot exists. Authors own this lifetime consequence; the host
  does not clear retained chat metadata to force collection. Unconfirmed collection requires RestartRequired.
- Keep the original function strongly owned by its registration and only weakly referenced by proxies.
  Clone operations preserve that weak target and activation-specific admission callback. A short-lived
  target clone supplies the snapshot's function/plugin aliases to SK's normal execution implementation.
- Use the existing build-time SK patch to forward the execution core. Preserve the original public
  invocation pipeline, kernel, filters, argument binding, service injection, cancellation and streaming.
  Do not clone kernels, clear filters, parse arguments or convert results in the proxy.
- Rebind `FunctionResult.Function` to the proxy using SK's copy constructor. Preserve the original
  value, culture, metadata and rendered prompt. Do not replace mod exceptions with text exceptions.
  The AIFunction entry delegates its existing SK argument/result handling to the original function.

Closing registration rejects new calls before entering invocation filters and removes discovery.
Its asynchronous disposal waits for admitted discovery, calls, post-invocation filters and streaming
enumerator disposal. Permission/rendering callbacks are also excluded from retirement. The mod
entry then disposes its tool and underlying resources; the manager does not dispose them prematurely.
Old proxies stay invalid after a replacement activation with the same mod/function names.
Optional advisory usage tracking is not part of this contract.

Tool callbacks use BeforeCallHook and PermissionConsentHook. BeforeCallHook receives the original
FunctionCallContent before approval, including calls without arguments, and returns optional display
content. It must not perform the operation because approval can still deny the call.
PermissionConsentHook receives FunctionCallContext and a per-invocation PermissionConsentRequest.
It may change HeaderKey, RememberMasks and CustomOptions. True allows, false denies, and null
continues the host's normal approval flow using the modified request. Existing bypass rules and
FullAccess take precedence. Auto uses the existing reviewer; the hook does not open dialogs or
write remembered rules. Both hooks are synchronous with no UI-thread guarantee. Registered
snapshots forward them through weak references under the registration gate; closure prevents
further mod callbacks. Request values can outlive the callback and must not retain mod objects.

McpChatFunction supplies a static permission hook that prepares the MCP heading and the
ToolConsentOption.BypassPluginApproval choice. ChatService applies that choice to the snapshot's
stable Key for any tool group, without testing the original ChatPlugin type. Approval does not use an
IsMcp discriminator. Server connection management remains specific to MCP.

Model-facing descriptions do not promise transparent CLR reflection: proxy ParameterType and
UnderlyingMethod are absent. Code that needs the actual CLR metadata must access the original
function while its activation is valid. Retaining that function or its types can prevent unloading.
`NativeChatFunction` accepts optional JsonSerializerOptions and passes them directly to SK, allowing
mod-owned converters and generated JSON metadata without introducing another binding pipeline.

SK 1.75's method converter cache also strongly retains parameter Type keys. A separate build-time
Core patch changes its cache call to use weak keys for collectible types, preserving the upstream
converter factory unchanged. Noncollectible types keep the original cache. Both SK patches validate
the pinned version and relevant method shape; upgrades require review. No cache is force-cleared.

This contract removes implicit execution references from function descriptions and snapshots; it does not make
explicit DefaultValue or AdditionalProperties values, arbitrary result values, result metadata,
exceptions, arguments, filters, UI objects or third-party
caches collectible. Preserve the existing chat persistence pipeline. For predictable hot unloading,
return host-owned data or already materialized JSON, and use generated serialization where reflective
caches would otherwise retain mod types. Such data choices belong to the mod, not an automatic
host conversion. A string is one simple host-owned result shape. Unconfirmed collection still requires restart.

Persistent chat/localization data must use host-owned key/value types. Formatted and aggregate
locale keys copy their argument containers: C# collection expressions can otherwise create a
collection type inside the mod and keep its assembly alive even when all elements are host values.
This does not make mod-defined keys, arbitrary object values or UI controls safe to retain.

## Author workflow

Derive your tool group from ModChatPlugin and populate it with NativeChatFunction instances.
BuiltInChatPlugin is reserved for application-owned tools; both share NativeChatPlugin. The manager
exposes them together through NativePlugins. A group named `everything` has Key `mod.everything`.
Choose a stable group name that does not collide with another installed mod's group. Key is used
by persisted settings and approval rules, independently of names deduplicated for the model.

Resolve `IChatPluginManager` from host services and call `RegisterChatPlugin(yourTool)`.
Register its returned IAsyncDisposable handle with the context. For an entry that owns ongoing
queries, signal its stop source, dispose the handle to revoke and drain calls, then dispose the
underlying connection/tool in entry disposal. The handle supports repeated disposal, so subsequent
hot-unload reversal is safe. This also releases queries on ordinary exit, when the general reversal
queue is deliberately skipped.

Discovery exposes your actual tool instance, including its settings, warnings and dynamic
`GetAvailableFunctionsAsync` implementation. Each new chat scope uses that discovery and the normal
enablement rules. Existing scopes retain their selected tools through weak, revocable function proxies.
Use those scopes for chat execution; retaining the real instance from the discovery list retains the mod.

Closing the handle removes discovery, rejects new calls through old proxies and waits for admitted
calls, post-invocation filters and streaming enumerators to finish or be disposed. Close it before
disposing connections or the tool itself. A newly registered activation does not revive old proxies.
Permission and friendly-rendering callbacks are also revoked. UI contributions still need their own
removal/disposal; this registration does not make arbitrary controls or display objects collectible.

Your methods keep the normal SK execution behavior: complex parameter binding, optional defaults,
injected services, filters, cancellation and streaming. The host does not parse arguments, clone the
kernel, convert result values or replace exceptions. It only changes FunctionResult.Function to refer
to the proxy, so a stored result does not retain the original method through that property.

Pass callbacks directly to NativeChatFunction using `permissionConsentHook` and `beforeCallHook`:

- `PermissionConsentHook` receives FunctionCallContext and PermissionConsentRequest when existing
  rules have not bypassed approval. Return true to allow, false to deny, or null to continue normal
  approval. You may customize the request's HeaderKey, RememberMasks and CustomOptions. Obtain the
  original call from `context.FunctionCallContent`. Use ToolConsentOption.BypassPluginApproval for
  a host-handled option to remember approval for the current tool group; arbitrary option handlers
  are not part of outer approval. The host owns dialogs, automatic review and rule persistence.
- `BeforeCallHook` receives the original call before approval, even when Arguments is null or empty.
  Return a ChatPluginDisplayBlock to display, or null. The call may still be denied, so do not execute
  the tool operation here. Use host-owned display types and values that can outlive your mod.

Both hooks are synchronous and do not imply UI-thread execution. The host forwards registered hooks
without retaining mod delegates in chat snapshots and stops forwarding when registration closes.
Consent requests can stay alive while a user decides: their titles, options and values must not keep
mod-defined objects alive. Existing FullAccess and bypass rules take precedence over the hook.
Returning null in Auto mode requests the existing automatic reviewer rather than opening a dialog.

The proxy exposes a persistent description, not the original CLR reflection surface. Names,
descriptions, required flags and SK-generated schemas remain available; ParameterType and
UnderlyingMethod do not. Serializer options are not copied into the description.
Parameter metadata's `DefaultValue` is assigned directly, without a type whitelist, serialization or
conversion. Reference-type defaults keep the original object reference, including mutable objects.
If a default is a mod-owned object or contains references to mod objects or types, a retained
chat snapshot can prevent unloading. You are responsible for these references; the host will not
erase defaults to force collection, and unconfirmed collection results in `RestartRequired`.
SK's ordinary method-derived defaults already use strings. Actual argument/default binding remains
SK's job; setting description metadata does not add an optional parameter to the underlying method.

Function metadata's `AdditionalProperties` is shallow-copied into a new host-owned dictionary for each
proxy and clone. All keys and values are preserved without filtering or serialization. Changing which
entries exist in the original dictionary does not change an existing snapshot, but objects stored as
values are still shared. Strings and Boolean flags do not themselves retain your mod. Mod-owned
objects, delegates, `Type` values and collections containing them can prevent unloading for as long as
the chat snapshot is retained. You are responsible for these references; the host will not erase chat
metadata to force collection. If collection cannot be confirmed, the result is `RestartRequired`.

For complex mod-owned parameter types, you can pass `jsonSerializerOptions` to NativeChatFunction.
These options go directly to SK for schema generation and binding. For example:

```csharp
new NativeChatFunction(
    Search,
    ChatFunctionPermissions.FileRead,
    jsonSerializerOptions: SearchJsonContext.Default.Options);
```

Use your normal System.Text.Json source-generated context or converters; include metadata for the
parameter and return types SK needs. The host patches SK's strong parameter-converter cache to use
weak keys for collectible types, without changing conversion behavior.

Results remain the objects your method returns. Prefer host-owned result types, strings or a prebuilt
JsonElement when chat history must survive mod removal. A mod-defined result, exception or
metadata value held by history can still retain the mod. Reflection-based serialization elsewhere
can also retain type/accessor caches; generated serialization avoids that particular path. Existing
chat persistence rules still apply. The proxy does not promise that every object graph can unload,
and the engine reports RestartRequired when collection cannot be confirmed.

Use host-owned locale keys and ordinary strings/numbers in persistent messages. Do not put a
mod enum, Type or custom object into a DirectLocaleKey. FormattedDynamicLocaleKey and
AggregateDynamicLocaleKey copy the collection container, because a compiler-generated collection
inside a mod can otherwise retain its assembly even when each element is a host-owned key.
That copy does not remove references inside mod-defined elements.
