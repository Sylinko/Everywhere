# Everywhere Harness Architecture

Everywhere's agent runtime (`Everywhere.Harness` inside `Everywhere.Core`, paired with UI/session orchestration in `Everywhere.AI` and `Everywhere.Chat`) is built directly on **`Microsoft.Extensions.AI` (MEAI)** with zero `Microsoft.SemanticKernel` dependency.

## Layered Responsibilities

```
ChatService (Agent Loop, Tool Approval, Context Compression, Image Recovery)
  ├─ Harness/Context/     (ChatHistoryBuilder, ChatImage, CompressionBoundary, ContextUsage)
  ├─ Harness/             (ResponseAssembler, ReasoningMetadata)
  ├─ Harness/Tools/ & Mcp/(ToolBinding, ToolRecords, ManagedMcpClient)
  ├─ Harness/Execution/   (GenerationContext, FunctionCallContext)
  ├─ Harness/Sessions/    (ModelSession, ModelSessionFactory -> IChatClient)
  ├─ Harness/Diagnostics/ (ChatExceptionNormalizer, ChatExceptionEvidenceExtractor)
  └─ Harness/Providers/   (GeminiGenerateContentClient, GeminiInteractionsClient, MistralChatClient)
```

### 1. Protocol, Execution, & Tool Primitives (`src/Everywhere.Core/Harness`)
- **`ResponseAssembler`**: Consumes streaming `ChatResponseUpdate` items for one model attempt, assembling contiguous `TextContent` and `TextReasoningContent` blocks, respecting explicit block boundaries (`reasoningItemId`, Anthropic `content_block_stop`, Gemini `step.stop`), and collecting completed client `FunctionCallContent` items (`InformationalOnly == false`). Early tool-call UI activity ("calling tools") is signaled on the first protocol tool frame via `ModelSession.HasToolCallUpdate`.
- **`ReasoningMetadata`**: Defines the dialect-scoped metadata keys persisted directly on `AssistantChatMessageReasoningSpan.Metadata` and `AssistantChatMessageFunctionCallSpan.Metadata`:
  - `dialect`: Originating wire protocol (`openai`, `openai-responses`, `anthropic`, `gemini`, `gemini-interactions`, `mistral`, `ollama`).
  - `protected`: Provider cryptographic signature or encrypted reasoning payload (`TextReasoningContent.ProtectedData`).
  - `itemId`: Native reasoning item identifier (e.g., OpenAI Responses `rs_...`).
  - `redacted`: Marks opaque redacted thinking blocks (e.g., Anthropic `redacted_thinking`).
  - `gemini.step`: Preserves native Gemini Interactions hosted-tool steps (such as `google_search_call` and `google_search_result`) across multi-turn continuations.
  - `thoughtSignature`: Preserves call-level Gemini `generateContent` `thoughtSignature` metadata on `ToolCallRecord.Metadata` across multi-turn tool replay (and supports pre-Harness `chat-v2.db` turns).
- **`Tools/`**:
  - `[ChatFunction]` & `[FromChatServices]`: Native attributes used by built-in and platform plugins. `NativeChatFunction` builds `AIFunction` instances via `AIFunctionFactory.Create` with `ExcludeResultSchema = true` and `ConfigureParameterBinding` so service-injected parameters never leak into wire JSON Schemas.
  - `ToolBinding`: Exposes a non-executable `AIFunctionDeclaration` (with deduplicated wire name) to `ChatOptions.Tools`, while retaining the underlying `AIFunction` (built-in delegate or `ManagedMcpClientTool`) so tools can only run after host consent/approval.
  - `ToolCallRecord`, `ToolResultRecord`, `ToolContentResult`, `ToolResultCapture`: Persist tool calls, rich MCP content blocks, blob-backed images (`ToolResultFile`), `PromptNode` trees, and JSON values in MessagePack (`chat-v2.db`) without duplicating message bodies.
- **`Mcp/`**: Managed Model Context Protocol client transport (`ManagedMcpClient`, `ManagedMcpClientTool`, `McpServiceExtensions`).
- **`Execution/`**: Runtime generation & invocation scopes (`GenerationContext`, `FunctionCallContext`).
- **`Providers/`**: Native stateless `IChatClient` implementations for protocols without an official MEAI SDK:
  - `GeminiGenerateContentClient`: Stateless Google Gemini `generateContent` / `streamGenerateContent?alt=sse` client (`ModelProviderSchema.Google`, dialect `"gemini"`), supporting OpenAPI 3.0 tool schema transformation (`TransformToOpenApi3Schema`), `thought: true` and call-level `thoughtSignature` continuation, sampling/thinking options, and portable JSON fallback for foreign tool turns.
  - `GeminiInteractionsClient`: Stateless Google Gemini Interactions API client (`ModelProviderSchema.GoogleInteractions`, dialect `"gemini-interactions"`, `POST /v1/interactions`, `store: false`), supporting signed `thought` steps, hosted search steps, inline media, sampling/thinking options (`Temperature`, `TopP`, `ThinkingLevel`, `IncludeThoughts` via `GoogleInteractionsOptions`), and portable JSON fallback for foreign tool turns.
  - `MistralChatClient`: Native Mistral Chat Completions client supporting `reasoning_effort`, structured `thinking` content chunks, streaming tool-call argument assembly, and token usage reporting.

### 2. Model Sessions & Diagnostics (`src/Everywhere.Core/Harness/Sessions` & `src/Everywhere.Core/Harness/Diagnostics`)
- **`Sessions/` (`ModelSession`, `IModelSessionFactory`, `ModelSessionFactory`, `ModelConnection`, `ChatRequestUpdate`, `OpenAICompatibleToolSchemaTransformer`)**: Captures an immutable snapshot of `AssistantConfiguration`, `ModelSchemaOptions`, and `ModelConnection` for one logical operation.
  - Concrete sessions: `OpenAIModelSession`, `OpenAIResponsesModelSession`, `AnthropicModelSession`, `GoogleModelSession`, `GoogleInteractionsModelSession`, `MistralModelSession`, `OllamaModelSession`.
  - `StreamRequestAsync`: Clones input `ChatMessage` and `ChatOptions` per attempt so transport retries send identical wire payloads, manages OpenTelemetry `Activity` lifecycles, normalizes HTTP/SDK/completion errors via `ChatExceptionNormalizer`, and enforces bounded exponential backoff.
- **`Diagnostics/` (`ChatExceptionNormalizer`, `ChatExceptionEvidence`, `ChatExceptionEvidenceExtractor`, `ChatExceptionRecovery`, `ChatExceptionDiagnostics`)**: Extracts HTTP, SDK, and completion stop-reason evidence into structured retry/recovery policies.
- **Adding a New Protocol**: Implement `IChatClient` (or wrap an SDK's MEAI `IChatClient`), add a `ModelSession` subclass under `Harness/Sessions/` with a unique `Dialect` name, and register its `ModelProviderSchema` branch in `ModelSessionFactory`.

### 3. Conversation Context & Cross-Provider Replay (`src/Everywhere.Core/Harness/Context` & `src/Everywhere.Core/Chat`)
- **Single Source of Truth (`src/Everywhere.Core/Chat/Messages`)**: `AssistantChatMessage.Spans` in `chat-v2.db` is the sole durable representation. No shadow replay envelopes or string-offset sidecars are stored.
  - Signature-only or redacted reasoning blocks with no visible text are stored as empty-text `AssistantChatMessageReasoningSpan` instances carrying `ReasoningMetadata`; `ChatPresentation` filters reasoning rows by `HasOutputContent` so opaque continuation spans never render blank UI bubbles.
- **`Context/` (`ChatHistoryBuilder`, `ContextPrefixState`, `ChatImage`, `CompressionBoundary`, `ContextUsage`, `VisualContextReferenceTracker`)**:
  - **In-memory epoch prefix locking (`ContextPrefixState`)**: Freezes `(Tools, SystemPrompt)` per active conversation epoch on `ChatContext` so KV caches stay intact across multi-turn chats and tool loops, injecting environment/skill/disabled-tool updates at anchored tail positions via `ChatRole.User` `<system-reminder>` messages. See [ContextImmutability.md](ContextImmutability.md).
  - **Roadmap**: See [QueueAndSteer.md](QueueAndSteer.md) for the planned input queueing, mid-turn steering, and opt-in detachable background tools design.
  - **Same dialect**: Reconstructs `TextReasoningContent` with `ProtectedData`, `reasoningItemId`, `redacted`, and native step properties intact.
  - **Switching to `anthropic`**: Strips unsigned or foreign reasoning spans; preserves Claude 4.6 signature-only `ThinkingBlockParam` vs `RedactedThinkingBlockParam` distinction.
  - **Switching to `openai-responses`**: Requests `reasoning.encrypted_content` on outbound calls and replays `rs_...` encrypted reasoning items only for matching `openai-responses` spans; omits foreign reasoning.
  - **Switching to `gemini` or `gemini-interactions`**: Replays signed `thought` and `functionCall`/`function_call` turns for matching Gemini dialect turns; automatically degrades foreign or unsigned historical tool turns into portable text/media evidence so Gemini 3 thinking models never fail with `missing_thought_signature`.
  - **Switching to `openai`, `mistral`, or `ollama`**: Forwards readable reasoning summaries (`!redacted && Text.Length > 0`) as plain `TextReasoningContent` without foreign cryptographic tokens.
