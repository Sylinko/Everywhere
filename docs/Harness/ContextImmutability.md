# Context Immutability & Prefix Cache Stability

## Motivation

Modern LLM providers (Anthropic Prompt Caching, OpenAI / OpenAI Responses automatic prefix caching, and Gemini implicit context caching) match KV-cache prefixes strictly in wire order:

1. **Tool declarations** (`tools`)
2. **System / developer instructions** (`system`, `instructions`, `systemInstruction`)
3. **Conversation messages** (`messages[0..n]`)

Any mutation near the front of that payload invalidates all subsequent cached tokens down to zero. Previously, `ChatService` rebuilt system prompts (including dynamic `{Date}`, `{SkillsPrompt}`, and `{WorkingDirectory}` values) and tool lists statelessly on every generation, causing avoidable cache misses when:

- Plugin/MCP discovery altered declaration ordering;
- Calendar `{Date}`, system prompts, or skills changed mid-conversation;
- Tools or MCP plugins were disabled mid-conversation;
- A `Strategy` supplied a temporary `SystemPrompt` override for one turn and the next turn returned to the default prompt.

## Why In-Memory Per-Epoch State (Zero Persistence Schema Change)

Provider prompt caches operate on short physical TTL windows (typically 5–10 minutes of inactivity, up to 1 hour with long-retention options). Reopening an older conversation from `chat-v2.db` after idle expiration or app restart already incurs an unavoidable cold cache miss.

By holding prefix state **in memory on `ChatContext`** (`ContextPrefixState`) instead of adding persistent sync/MessagePack envelopes:

- Active multi-turn conversations and multi-step tool loops keep byte-for-byte identical `(tools, system)` prefixes across consecutive requests;
- Reopened or long-idle conversations cleanly re-establish a fresh baseline without accumulating historical delta patches or stale system instructions indefinitely;
- Zero changes to `chat-v2.db` schema or cross-device sync contracts are required.

## Design Specification

### 1. Deterministic Tool Declaration Ordering
`ChatPluginScope` sorts `Declarations` lexicographically by `AIFunctionDeclaration.Name` (`StringComparer.Ordinal`). Because every declaration name in a scope is already deduplicated, wire ordering is deterministic regardless of plugin registration or async MCP discovery order.

### 2. Epoch Boundaries (`ContextPrefixSnapshot`)
Each `ChatContext` owns one `ContextPrefixState`, maintaining an active `ContextPrefixSnapshot` bound to:

- **Provider identity**: `Dialect`, `ModelId`, `SupportsToolCall`
- **History head boundaries**: `ActiveSummaryNodeId` and `WindowStartNodeId` (exposed by `ChatHistoryBuilder.SelectContextMessagesAsync`)
- **Freshness**: `LastUsedAt` bounded by an in-memory TTL (`10 minutes`)
- **Initial prompt & locked tools**: `InitialSystemPrompt` (captured directly from `history[0]` without coupling `ContextPrefixState` to prompt template parameters) and `LockedTools` (with per-tool `(Description, JsonSchema)` fingerprints)

An epoch **resets** cleanly (rebuilding fresh `tools` + `messages[0]` and clearing accumulated reminders) when any of the following occurs:

1. No active snapshot exists or `UtcNow - LastUsedAt > CacheTtl` (cold cache);
2. `Dialect`, `ModelId`, or `SupportsToolCall` changed;
3. `ActiveSummaryNodeId` changed (context compression completed or branch switched across a checkpoint) or `WindowStartNodeId` changed (sliding-window `MaxContextRounds >= 0` evicted older turns, or user edited/switched before the first selected node);
4. The first captured message (`messages[1]`, excluding `System`) diverged from the epoch's baseline (`commonPrefixLength == 0`, e.g. image-recovery offload on the initial message);
5. **Additive or mutated tools**: the current `IChatPluginScope` introduces any tool not present in `LockedToolDeclarations`, or an existing tool's description or JSON schema changed. (Non-additive tool protocols across custom proxies and 7 provider schemas cannot inject new executable tool schemas mid-message-stream reliably without resetting.)

### 3. Preserving Prefixes Across Mid-Epoch Changes
When an epoch stays valid across consecutive requests:

1. **Immutable Header**:
   - `history[0]` (`System`) is forced back to `InitialSystemPrompt` byte-for-byte.
   - `ChatOptions.Tools` is forced back to `LockedTools` (`ChatToolMode.Auto` if non-empty), even when tools or plugins were disabled mid-epoch.

2. **Host-Enforced Soft Tool Disabling**:
   - Disabled tools are omitted from the live `GenerationContext.PluginScope`, while remaining present in wire `ChatOptions.Tools` to preserve byte-0 prefix caching.
   - A `<system-reminder>` informs the model which tools are now disabled and must not be invoked.
   - If the model still emits a disabled tool call, `ChatService.InvokeFunctionsAsync` (`ChatService.Tools.cs`) already checks `scope?.TryGetBinding(...)` and safely refuses execution without side effects.

3. **Surgical Tail Reminders (`<system-reminder>` as `ChatRole.User`)**:
   - All tail reminders use `ChatRole.User` (matching `VisualContextResetChatMessage`) rather than `System` or `"developer"`, because providers such as Gemini `generateContent` (`GeminiGenerateContentClient`) hoist `system`/`developer` messages out of the message array into top-level `systemInstruction` regardless of position.
   - System prompt deltas compare `history[0].Text` (`current`) against `previous` (`AppliedSystemPrompt` of the last surviving reminder, or `InitialSystemPrompt`):
     - If `current == previous` (including line-ending-only differences), **no** prompt reminder is emitted.
     - If `current == InitialSystemPrompt` (e.g., a temporary Strategy `SystemPrompt` override ended), a concise one-line notice restores the original system instructions.
     - Otherwise, `DiffPlex` (`LineChunker`) computes a compact unified diff (`@@ -a,b +c,d @@` with 1 line of context) so minor mid-epoch updates (e.g., `{Date}`, `{WorkingDirectory}`, or `{SkillsPrompt}` changes) emit only the changed lines without repeating unchanged sections or coupling `ContextPrefixState` to prompt placeholders. If no lines match or the diff is not shorter than `current`, the updated instructions are emitted directly.

4. **Stable Anchor Positioning & Branch Backtracking (`Hash128`)**:
   - Each captured conversation message (`captured[0..n]`, excluding `System` and reminders) is hashed into a 128-bit content fingerprint (`Hash128`).
   - Finding `commonPrefixLength` between `LastRequestMessageHashes` and `currentHashes` preserves every historical reminder anchored at `AfterMessageCount <= commonPrefixLength` at its exact wire index (so Turn 3+ continues hitting KV cache through Turn 2's reminders and replies), while automatically pruning reminders from abandoned branches (e.g., `Edit` or `Retry`) and rolling back the effective baseline state.
   - Any newly produced reminder anchors at:
     - `captured.Count - 1` (immediately before the new trailing `User` message) when a new user message was appended (`commonPrefixLength < captured.Count && captured[^1].Role == ChatRole.User`), or
     - `captured.Count` (at the tail after tool results / assistant continuation) during mid-turn tool loops or `Continue`.
   - **Context compaction prefix reuse & batch tool-call guard**:
     - `RequestCompressionSummaryAsync` projects the active epoch read-only via `ContextPrefixState.ProjectReadOnly(...)`, preserving `LockedTools` (`tools`) + `InitialSystemPrompt` + surviving anchored reminders alongside `ChatToolMode.None` across all 7 provider adapters (so automatic compaction immediately following a warm turn hits the existing KV cache without mutating the epoch snapshot). Once `ContextLengthExceeded` trims the source head (`commonPrefixLength == 0`), projection returns `null` so locked tools are omitted to free context tokens.
     - If a compaction model ignores `ChatToolMode.None` (or uses a protocol without `tool_choice: "none"` such as Ollama) and emits a batch of tool calls, the entire batch counts as one strike: the host injects corrective `FunctionResultContent` errors for every call in that batch and retries once; a second tool-call batch fails compaction with `ContextCompression_Error_ToolCallNotAllowed`.
   - Title generation (`GenerateTopicAsync`) uses an independent system prompt and does not read or mutate `ChatContext.PrefixState`.

## Future Optional Extension
If cross-restart prefix preservation or audit visibility is ever needed, persisted `ContextDeltaChatMessage` records (`MessagePackUnion` with `IsHidden => true`, modeled after `VisualContextResetChatMessage`) can be layered later without altering existing `chat-v2.db` tables.
