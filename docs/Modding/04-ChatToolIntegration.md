# Chat Tool Integration (Harness & MEAI)

**Status:** implemented on the Harness + `Microsoft.Extensions.AI` (`AIFunction` / `ToolBinding`) pipeline, with no Semantic Kernel dependency.

## 1. Registration Contract

`NativeChatPlugin` is the shared base for in-process managed tool groups:

- `BuiltInChatPlugin` (`builtin.{Name}`): application-supplied built-in tools.
- `ModChatPlugin` (`mod.{Name}`): mod-supplied tools registered dynamically via `IChatPluginManager.RegisterChatPlugin(ModChatPlugin)`.
- `McpChatPlugin` (`mcp.{Id}`): remote/stdio MCP tools with their own lifecycle.

Both built-in and mod plugins appear in `IChatPluginManager.NativePlugins` and the **Application tools** tab in `ChatPluginPage`. Tool keys (`mod.{Name}`) identify persisted enablement and approval rules and must remain stable across mod updates and activations.

`IChatPluginManager.RegisterChatPlugin(ModChatPlugin)` returns an `IAsyncDisposable` registration handle (which should also be passed to `context.Register(registration)`):

- **Closing on unload**: `DisposeAsync()` marks the registration closed, removes the plugin from `NativePlugins` (when the manager itself is not already disposed), clears internal strong references to mod functions, rejects new calls with `InvalidOperationException("This chat plugin has been unloaded.")`, and waits asynchronously until all in-progress tool invocations complete.
- **Settings UI detachment**: `ChatPluginPageViewModel` listens to `ToolSettings.Plugins.CollectionChanged` and clears `SelectedPlugin` and `SettingsTabItem.SettingsItems` when a mod plugin disappears, allowing retained views to drop their settings bindings immediately.

## 2. Authoring a `ModChatPlugin`

Declare methods with `[ChatFunction("...")]` and `[FromChatServices]` (same attributes as built-in plugins):

```csharp
public sealed class ExampleChatPlugin : ModChatPlugin
{
    public ExampleChatPlugin() : base("example")
    {
        _functionsSource.Add(new NativeChatFunction(SearchAsync, ChatFunctionPermissions.FileRead));
    }

    [ChatFunction("search_items")]
    [Description("Search items exposed by the mod.")]
    [DynamicLocaleKey(LocaleKey.Example_Search_Header, LocaleKey.Example_Search_Description)]
    private async Task<string> SearchAsync(
        [FromChatServices] IChatPluginDisplaySink displaySink,
        [Description("Query string.")] string query,
        CancellationToken cancellationToken = default)
    {
        await displaySink.AppendDynamicLocaleKeyAsync(new DirectLocaleKey($"Searching: {query}"));
        return "done";
    }
}
```

- Override `ChatPlugin.SettingsItems` to show tool-group settings directly inside `ChatPluginPage`'s **Settings** tab without requiring a separate settings page.
- Pass source-generated `JsonSerializerOptions` to `NativeChatFunction` when tool parameters use mod-defined complex types (`JsonSerializerContext.Default.Options`) so schema generation stays reflection-free inside the mod assembly.

## 3. Revocable MEAI Proxy and ALC Unloading

When `ChatPluginManager.CreateScopeAsync` builds a chat turn scope (`IChatPluginScope`):

1. Mod plugins enter a discovery lease (`TryEnterInvocation`) — if a concurrent unload has already closed the registration, the plugin is skipped cleanly.
2. Each admitted `NativeChatFunction` is wrapped by `ChatPluginRegistration.CreateFunction`:
   - `RevocableAIFunction` (subclass of `AIFunction`) eagerly copies `Name`, `Description`, `JsonSchema`, and `ReturnJsonSchema` (pure strings / `JsonElement` values).
   - `RevocableAIFunction.UnderlyingMethod` returns `null`, and `JsonSerializerOptions` inherits host-owned `AIJsonUtilities.DefaultOptions`, avoiding any reference to mod `MethodInfo` or `JsonSerializerContext`.
   - It holds only a `WeakReference<NativeChatFunction>` and a reference to `ChatPluginRegistration`; `InvokeCoreAsync` acquires an active-call lease via `EnterInvocation()`, checks cancellation, and delegates to the target `AIFunction`.
   - `BeforeCallHook` and `PermissionConsentHook` are wrapped in closed-checked forwarding delegates (`null` / `false` once retired).
3. `ChatPluginSnapshot` copies display metadata eagerly (`Key`, `HeaderKey`, `DescriptionKey`, `Icon`, `BeautifulIcon`, `IsMcp`) without retaining the original `ChatPlugin` subclass, and passes itself (`this`) into `ToolBinding`.
4. `ChatFunction.CreateNativeFunction` checks `method.Method.Module.Assembly.IsCollectible`: when a collectible mod does not supply explicit `JsonSerializerOptions`, it clones `new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)` per function so MEAI's static `ReflectionAIFunctionDescriptor._descriptorCache` (`ConditionalWeakTable<JsonSerializerOptions, ...>`) does not pin collectible mod assemblies into the host-wide `AIJsonUtilities.DefaultOptions` singleton.
5. `ToolBinding` extracts `Declaration` via `AIFunctionFactory.CreateDeclaration`, ensuring stored chat scopes and `FunctionResultContent` never keep collectible CLR metadata alive after `DisposeAsync()`.
