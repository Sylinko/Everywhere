# LLM Request Recovery and Diagnostics

Status: architecture proposal, with confirmed product decisions recorded below. Runtime behavior has not been changed by this document. Source reviewed on 2026-10-02.

## 1. Scope and terminology

A **turn** starts with a user message and includes subsequent assistant reasoning, output, tool batches, and automatic context compression until generation finishes. It may last many minutes. A generation context is a runtime resource owner; manual Continue can create a new generation context while continuing the same conversational turn.

A **logical request** is one assistant-service request with a fixed input and execution settings. An **attempt** is one transport execution of that request. A **tool batch** is the complete set of calls accepted from one assistant response, including parallel calls in the protocol even when local execution is sequential.

This design covers request error classification, automatic request retries, progress presentation, bounded diagnostics, and refactoring existing callers onto the same execution boundaries. Main chat, subagents, context compression, approval and topic generation are all part of the integration scope. Their business decisions remain distinct, but duplicated request loops, swallowed cancellation, ad-hoc classification, and ambiguous generation-result contracts must be replaced rather than preserved behind the new layer. No mock server or new dependency is introduced at this stage. [ErrorClassification.md](ErrorClassification.md) defines the proposed heuristic rules and source examples.

## 2. Recovery contracts

### Existing user actions

- `ChatService.Retry` creates a branch at the selected assistant node. This is an explicit user action that regenerates from that branch point; it must not become the implementation of automatic request retry.
- `ChatService.Continue` appends a new assistant message and resumes from the available history. Its purpose is best-effort preservation of progress within the turn, including tool results, useful partial output, and the effective compressed context.
- Automatic request retry repeats only the failed logical request. It does not create a branch, restart `GenerateAsync`, rebuild the entire turn, or replay executed tools.

The same assistant message currently can contain several request/tool cycles. Therefore neither `ErrorMessageKey != null` nor a runtime exception is sufficient reason to exclude the entire message from context.

### Tool-call/result invariant

For every accepted tool-call ID, the next provider-facing history must contain exactly one corresponding result, whether successful, failed, denied, canceled, or unavailable. The representation may aggregate results into one SK tool message; the invariant concerns individual call IDs and results, not the number of local message containers.

```text
Assistant: [call1, call2, call3]
Tool: result1
Tool: result2
Tool: result3
```

There are two valid recovery boundaries:

1. Before the response batch is accepted and any call executes, discard all tool-call fragments from that failed attempt and retry the request with fresh accumulators.
2. After calls are accepted or execution begins, preserve the batch and all known results. Supply explicit error/unknown-result entries for missing results and let the assistant decide how to proceed.

Do not discard a partially executed batch and regenerate it automatically. A missing result does not prove that a side effect did not happen. Error feedback must not claim an operation was never performed when its outcome is unknown. Ordinary C# cleanup cannot guarantee durable results after process termination; history reconstruction remains a necessary last boundary.

Current `InvokeFunctionsAsync` registers the entire batch before execution and stores each returned result before observing cancellation. `ChatHistoryBuilder` projects one result for each call and synthesizes an error result if storage contains no result. Preserve these properties during refactoring.

The outer request/tool cycle is sequential: finish an LLM request, accept its complete batch, execute/settle the tools, then send a new LLM request containing all results. There is no outer LLM request running in the middle of this batch and no need for an outer request-retry window around partially executed tools. A subagent or approval reviewer can perform its own requests/retries inside a tool invocation; those attempts are transparent to the outer LLM. A failed next request simply resends the same complete history. Tool failure/cancellation and process interruption still require call/result pairing, but are distinct from request retry.

### Failed output and Continue

An automatic retry uses the original logical request input, not that input plus the failed attempt's partial answer. Each attempt has independent text/reasoning/tool-call/metadata/usage accumulation. Previously completed request/tool cycles and compression checkpoints remain committed.

Failed attempts are provisional while automatic recovery is in progress. Do not append permanent failed-attempt messages or `AssistantErrorPresentationRow` instances for each attempt. Before replay, reset only the failed request attempt's output and accumulators; never remove earlier committed tool cycles or compression results. If a retry succeeds, discard the earlier failed output and temporary failure diagnostics from the conversation/presentation. Usage and invocation accounting still describe the actual requests made.

Only when automatic recovery terminates in failure (retry budget exhausted or a non-retryable failure) should the normal error path retain and expose the collected errors. "All errors" means all retained records within the agreed last-10 bound, with a total/omission count when earlier failures were evicted. Continue preserves prior committed progress and can retain useful partial output from the last unreplaced attempt, subject to protocol validity. Incomplete tool-call fragments must never be promoted into accepted calls merely to preserve partial output. Cancellation is a separate outcome, not exhausted recovery (section 6).

This requires a per-attempt output boundary, not a persistent list of discarded attempts or a blanket `ExcludeFailedMessages` rule. Implementation should use the existing message/span ownership to reset provisional output without changing previous committed content. Temporary diagnostics and retry progress are not serialized. If provisional content participates in incremental persistence, replay must also remove the superseded persisted content; runtime-only exclusion must not cause it to reappear after reload.

Do not add generation-failure notices to the LLM history for either automatic retry or Continue, particularly for network failures. Technical diagnostics remain outside prompt construction. Existing tool results still report their own success, errors, or missing/unknown outcomes as required by the protocol; no extra assistant/user notice is needed.

## 3. Responsibility boundaries

| Owner | Responsibilities | Excluded responsibilities |
| --- | --- | --- |
| Provider/SDK adapter | Extract HTTP status, structured error fields, message, retry delay, request ID and protocol completion information | Infer actual service identity from the wire protocol; decide conversation recovery |
| KernelMixin request infrastructure | Normalize request failures; execute attempts; apply retry policy; await backoff; honor cancellation; retain bounded diagnostics | Execute/replay tools; create chat messages or UI rows; compact history |
| ChatService | Snapshot logical request input; own message/attempt boundaries; consume streaming output; record statistics; commit complete tool batches; preserve turn progress | Reimplement provider-specific retry loops |
| Approval, compression, topic callers | Validate their business results and choose business recovery or failure feedback | Count transport retries as missing-decision corrections or file-read batches |
| Presentation | Project live request state and friendly errors; lazily display diagnostic details | Classify errors or schedule retries |

`HandledChatException` is the existing normalized error surface. Evolve its classification/extracted evidence rather than introducing a parallel taxonomy solely for retry. The original exception remains available for diagnostics. Retry eligibility is a policy decision using that evidence and the request context, not a property inferred from localized text.

The request executor catches failures only at its SDK request/enumeration/completion-validation boundary. Exceptions from UI projection, image persistence, statistics, or tool execution must not accidentally trigger another API request. A generic wrapper around an arbitrary `Func<Task>` does not express this boundary well.

Streaming remains the only generation path. A caller needs explicit attempt-start/failure/completion notifications as well as content, so it can reset accumulators and close the appropriate presentation output. An idiomatic `await foreach` stream with typed lifecycle updates is a candidate, not a committed API signature. The final failure must still propagate through the caller's normal exception path. Do not add both a final failure message in an event handler and a second message in the outer catch.

Protocol completion is separate from whether enumeration threw. Normalize length limits, filtering, provider errors, and interrupted transport separately. Missing finish metadata on a compatible endpoint is not automatically a failure. Only a response accepted as complete may commit its tool batch. Approval can reject an incomplete decision even when ordinary chat can preserve the same response's useful partial text.

Context overflow exits the transport retry loop for ChatService's compression recovery. Compression changes request input and starts a new logical request; it is not another identical-input attempt. Automatic approval still fails closed when its request ultimately fails, without handing off to manual approval. User cancellation propagates as cancellation rather than an approval denial or a retryable timeout.

## 4. Retry configuration and lifecycle

Confirmed configuration: `Assistant.RequestMaxRetries`, adjacent to `RequestTimeoutSeconds`, defaults to **5**; **0** disables automatic retries; **-1** means unlimited retries of eligible failures. Five retries means at most six attempts, including the initial request. Other negative values are invalid configuration. Unlimited retries do not make deterministic failures retryable.

Snapshot retry settings when creating the request infrastructure, consistently with the existing assistant configuration/timeout snapshot. A request-local override is appropriate for actual call-site needs, such as a connectivity check with no retry; avoid introducing a general policy registry without callers. Separate system assistants use their resolved settings; AutoSelect uses the selected current assistant's resolved settings.

Proposed backoff: exponential growth with jitter, starting at one second and capping the local backoff at 30 seconds. Honor valid server `Retry-After` values without retrying before the stated time. This local cap does not shorten a longer server delay. Exact jitter constants are not product decisions yet.

The cancellation token interrupts both request execution and delay. Distinguish caller cancellation from an attempt deadline; do not classify every `OperationCanceledException` as one or the other without token/deadline evidence. An attempt gets its own deadline lifecycle. Provider-specific first-response/stream-read timeout differences must be audited before describing the current single timeout setting as a universal end-to-end deadline.

Retries reset per logical request, not per tool call or entire turn. SDK retries must be explicitly reconciled with application retries so configured counts reflect actual requests. No retry counter reset merely because a failed stream yielded some content. A long-running `-1` loop must use bounded diagnostics and must not allocate one permanent UI row/message for every empty failure.

## 5. Retry presentation

Confirmed UX: a dedicated lightweight retry activity row following `ReasoningActivityItemPresentationRow` and the existing `ActivityMarker` presentation. Use a Lucide icon and separate primary/secondary headers:

- Header: `正在重试 3/5`, or `正在重试 3` for unlimited retry.
- `ActivityMarker.SecondaryHeader`: the latest user-friendly failure message, bound through a dynamic locale key; for example `与助手服务的连接中断。`.

Use the actual failure category; do not describe every failure as a connection interruption. Reuse the activity/group layout and running-state conventions, not the reasoning row's semantic type or reasoning Markdown content. SecondaryHeader is a view presentation slot, not an existing property on the base activity row. No raw exception message or stack trace belongs in this slot.

Proposed ownership:

- Each logical request owns its runtime progress and stable identity. It is not a single mutable global field on KernelMixin or GenerationContext: topic generation and nested approval/compression requests can have different lifetimes.
- ChatService associates progress with the owning assistant message/tool activity/turn. Reuse the existing busy-activity attachment and dispatcher boundary where suitable, while adding the dedicated row type.
- Keep one row identity across a recovery episode; update its count/text in place rather than appending a new row for every retry. Do not rebuild the entire turn for a changed count.
- During backoff and reconnection, generation remains busy and Continue is unavailable. Suppress a competing empty-response/pending row at the same location.
- The row represents the current recovery episode, including retry backoff/reconnection. It may hide while normal streaming output resumes, but it is not retained as a completed historical activity after recovery succeeds. Remove it on success, terminal failure, or cancellation. A later interruption of the same request reuses its progress identity and accumulated retry count.
- Nested requests attach to their actual owning activity. Background chat state stays with that chat; switching the visible chat does not transfer progress to another turn. Detached title generation should not overwrite foreground answer progress.
- Retry progress is not serialized and cannot reappear as a live operation after restart. The UI is a projection; it does not own a retry timer or cancellation source.

Repeated empty transport failures update the same retry row and bounded diagnostics without producing message nodes or permanent error rows. This coalescing behavior is confirmed, including for unlimited retries. The same no-permanent-attempt-errors rule applies when an attempt produced partial output and is replayed.

On terminal failure, remove the retry activity and use the normal `AssistantErrorPresentationRow` path once. The error presentation exposes the collected failures through a compact friendly summary and the details dialog, which lists every retained failure; do not duplicate terminal rows for individual retries. On successful recovery, neither a failed-attempt error row nor its diagnostic history remains attached to the conversation.

## 6. User cancellation

Confirmed direction: a user-stopped generation is a distinct persisted outcome, not an `AssistantErrorPresentationRow` containing `OperationCanceledException` text.

- Store an explicit serializable cancellation marker on the owning `AssistantChatMessage`; a Boolean such as `IsCanceled` is sufficient unless another concrete persisted outcome needs a shared enum. Existing messages default to not canceled.
- Project a dedicated cancellation/stopped row from this marker. Suggested text is `已停止`, with neutral styling and a Lucide icon. It remains visible after reloading the conversation without depending on a runtime exception or busy flag.
- Preserve useful partial output, completed tools, and context-compression progress. Do not clear an entire turn when the user stops during streaming, tool execution, approval, compression, or retry backoff.
- Preserve call/result pairing even when execution was interrupted. A tool's actual result or cancellation/unknown-outcome result remains part of protocol history. Removing a cancellation error banner does not remove required tool results.
- Cooperative cancellation associated with the caller's canceled token bypasses friendly-exception conversion, terminal error notification, and error-level logging for normal cancellation. Do not attach that cancellation exception to the assistant's error-details property. A coincidental late token cancellation must not hide an unrelated real exception.
- An attempt timeout remains a request failure unless the caller actually canceled. An arbitrary SDK `OperationCanceledException` without caller-cancellation evidence is not sufficient to mark the conversation as user-stopped.
- Remove live retry progress and discard its temporary failure records when the user cancels recovery. Do not flush those records as an exhausted-retry error after a deliberate stop. Existing unrelated errors remain intact.
- Finalize busy/span timestamps and retain canceled usage/statistics through the existing cleanup path. Cancellation does not imply zero tokens or no tool side effects.
- The latest stopped row can offer the existing Continue action under its normal ownership/read-only/busy guards. Continue appends normally; it does not erase the previous message's stop marker. Earlier stop rows remain process history and do not compete with the newest terminal action.
- The projection must distinguish stopped, failed, successful-empty, and running states. In particular, stopping before any output must not also show `NoResponsePresentationRow` or a stale pending/retry row.

The current broad `GenerateAsync` catch converts all exceptions into `ErrorMessageKey`; it needs a separate cooperative-cancellation path. Nested approval/compression/tool catches must preserve cancellation propagation and avoid generating a duplicate assistant error banner. This is targeted separation of canceled work from failure, not removal of all `OperationCanceledException` handling across the application. Do not infer new stop markers by matching previously persisted localized error strings.

## 7. Error details and resource limits

Confirmed direction: retain the concrete exception as an observable runtime-only property on `AssistantChatMessage`, ignored by both MessagePack and JSON. Persist `ErrorMessageKey` separately. The details button sits to the left of Continue and is visible only when diagnostics exist; historical error rows can show details even when Continue is unavailable.

The dialog is created lazily, with a bounded scroll region and selectable/copyable exception and stack information. Show attempt number, status/code, request ID, and classification evidence when available. Do not automatically add full request headers, credentials, prompts, or file contents to diagnostics. This local runtime detail surface does not imply synchronized exception storage.

Retain at most the **last 10 failure records per logical request**, plus scalar counts. No message-content deduplication is required. Drop old exception references wherever retained, including any message properties, rather than merely capping a secondary list. Do not recursively aggregate previous retry aggregates into the next exception.

For `AggregateException`, inspect/render with explicit node/depth/text budgets. Do not call an unbounded `Flatten()` or `ToString()` before truncation. A cap of 10 attempt records alone cannot bound the size of an individual provider exception; bound formatted details separately and identify truncation. Holding the raw exception can still retain a large object graph, which is a remaining limitation of retaining concrete exceptions.

The bounded records are provisional until terminal failure. Success or caller cancellation releases them instead of attaching a historical failure to the conversation. After terminal failure, its friendly error remains after runtime details are evicted or the conversation is reloaded. The runtime dialog lists all retained attempts, rather than exposing only the last inner exception; raw stacks remain non-serialized.

## 8. Friendly messages

Confirmed direction: stop automatically appending raw `Message`/`detailedMessage` to user-facing messages once the details dialog exists. Audit both `HandledChatException.FriendlyMessageKey` and `ExceptionExtensions.GetFriendlyMessage`, including aggregate/default branches. Do not convert an aggregate of ten diagnostics into ten paragraphs in an error row.

Use `IDynamicLocaleKey` for persistent presentation and parameterized localization for retry text. Prefer “助手” in UI copy; technical setting names such as `temperature` and `top_p` can stay in English. Do not infer system performance, geography, or the need for administrator privileges from a generic failure.

| Current Chinese wording (excerpt) | Proposed replacement |
| --- | --- |
| 操作超时。请检查您的系统性能，然后重试。 | 操作未能在规定时间内完成。请稍后重试。 |
| 请求服务超时，请重试。 | 等待助手服务响应超时。可以重试，或在助手设置中延长请求超时时长。 |
| 请求过于频繁、账户金额不足或者网络或地区不受支持。 | 助手服务暂时限制了请求频率。请稍后再试。 |
| 超出 API 使用配额……或者没有访问当前模型的权限。 | 助手服务的可用额度已用尽。请检查服务账户的余额或用量限制。 |
| 发生网络错误……亦或是服务提供商不支持您所在的地区。 | 与助手服务通信时发生网络错误。请检查网络或代理连接。 |
| 模型配置不正确……或当前模型不支持图像输入/工具调用。 | 助手服务未接受当前请求。具体原因可查看错误详情。 |
| 提供的服务端点无效。 | 助手的服务地址无效。请在助手设置中检查地址。 |
| 空响应……通常意味着存在网络或服务问题。 | 助手服务没有返回可用的响应。请重试。 |
| 访问被拒绝……或以管理员身份运行应用程序。 | 无法访问所需资源。请检查是否已授予相应权限。 |
| 参数是空的。请确保配置正确。 | 执行操作时缺少必要信息。具体原因可查看错误详情。 |
| 操作无效。请确保配置正确。 | 当前状态下无法完成此操作。具体原因可查看错误详情。 |
| 模型不支持 tempurature 参数……恢复到默认值可以解决此问题。 | 助手服务不接受当前的 temperature 设置。可以尝试恢复默认值。 |
| 模型不支持 top_p 参数……恢复到默认值可以解决此问题。 | 助手服务不接受当前的 top_p 设置。可以尝试恢复默认值。 |

These are copy proposals, not resource changes. Context-specific recovery messages must reflect what has already been attempted. A generic resource-limit response does not establish exhausted billing credit and must not use the quota-exhaustion wording without supporting evidence.

## 9. Current source anchors

- [ChatService](../../src/Everywhere.Core/Chat/ChatService.cs): Retry, Continue, generation/compaction boundaries, streamed accumulation, and batch registration/execution.
- [ChatHistoryBuilder](../../src/Everywhere.Core/Chat/ChatHistoryBuilder.cs): effective compression history and missing-result projection.
- [KernelMixin](../../src/Everywhere.Core/AI/KernelMixin.cs), [KernelMixinFactory](../../src/Everywhere.Core/AI/KernelMixinFactory.cs): request services and configuration snapshots.
- [HandledException](../../src/Everywhere.Core/Common/HandledException.cs), [ExceptionExtensions](../../src/Everywhere.Core/Extensions/ExceptionExtensions.cs): classification and friendly-message construction.
- [AssistantChatMessage](../../src/Everywhere.Core/Chat/Messages/AssistantChatMessage.cs): persisted error key and output spans.
- [ChatPresentation](../../src/Everywhere.Core/Views/Chat/ChatPresentation.cs), [rows](../../src/Everywhere.Core/Views/Chat/ChatPresentationRows.cs): stable row projection and runtime busy-activity attachment.
- [Tool approval architecture](../ToolApproval/Specification.md): reviewer-specific budgets and fail-closed behavior.

## 10. Implementation boundaries

The earlier product questions are resolved: successful retries leave no failed-attempt history, empty failures coalesce, retries use a reasoning-style activity with a friendly SecondaryHeader, and no failure notice is injected into LLM context. User cancellation gains a persistent marker and separate row. These decisions do not authorize runtime code changes by themselves.

The proposed concrete boundaries are described in [RuntimeDesign.md](RuntimeDesign.md): a local span/metadata checkpoint within the existing message, ordered asynchronous request lifecycle updates, a request-owned temporary activity scope, bounded terminal diagnostics, and separate cancellation completion for assistant and compression messages. API/type names and exact backoff constants can be settled during implementation without introducing more product states.

Verification for this document is source inspection only. No SDK integration test, mockllm server, runtime implementation, or live UI verification was performed.
