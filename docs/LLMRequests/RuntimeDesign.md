# LLM Request Runtime Boundaries

Status: technical design, not implemented. This refines the confirmed behavior in [Architecture.md](Architecture.md) and uses the rules in [ErrorClassification.md](ErrorClassification.md). Names below are proposed production API names, not existing symbols unless explicitly identified as current source. The intended result is one request/execution path used by existing features, not a retry wrapper surrounding all their historical exception and lifecycle behavior.

## 1. Keep the existing conversation ownership

Do not introduce one message node per transport attempt. Keep the current assistant message and sequential ChatService request/tool loop. Introduce a small attempt-output checkpoint inside the streaming consumer, scoped to one logical request.

Current `GetStreamingChatMessageContentsAsync` starts with `span = null` for each logical request and allocates fresh spans before appending output. Therefore it already avoids appending a new request's tokens into an older request's spans. `AssistantChatMessage.Count` reads the source list, while `Spans` is the dispatcher-observed projection; checkpoints must use the source count, not potentially delayed UI collection state. `MetadataDictionary` is backed by an immutable dictionary and can be saved/restored by value.

Capture before the first attempt:

- The source span count.
- The assistant's message-level metadata value.

On an eligible failure, after finishing that attempt's accounting and before delay/replay:

1. Remove only spans added after the checkpoint, through the existing `Edit`/change-set mechanism.
2. Restore message metadata to its checkpoint value.
3. Clear the local span reference, role, tool-call builder, attempt usage, first-token timing and temporary tool-argument activity.
4. Update the request's retry activity with its count and friendly error.

Every new attempt starts with fresh accumulation. Do not clear spans or tool results from earlier successful requests. Tool execution occurs after successful response validation, so it cannot append to this output region while that outer request is being retried. Nested subagents use their own chat/message ownership.

On success, retain the successful attempt's output. On terminal failure or caller cancellation during streaming, preserve useful output from the active attempt, without committing incomplete tool-call fragments. Cancellation during backoff has no active output to restore: the discarded attempt has already been reset. Never recover an older failed answer just because cancellation occurred before the next attempt started.

This checkpoint is a local lifetime rule, not a new persisted attempt graph, exclusion flag, general transaction framework, or reusable rollback service. It can remain a private ChatService helper if cleanup warrants a named type. Preserve normal incremental persistence notifications on removal/restoration. This is not a crash-atomic transaction: process termination during a write can leave the same kind of unfinished message that streaming already permits. Reopening must not depend on a runtime-only exclusion list.

Image output references belong to removed spans too. Dropping an attempt removes those references, not arbitrary files from blob storage; do not delete content-addressed data that other messages may share. Continue using the blob store's existing ownership/cleanup policy.

### Projection cleanup is part of rollback

Current `ChatPresentation.ChatTurnPresentation.Rewire` prunes `_activityRows` for removed reasoning/tool sources. `_outputRows` also caches text/image rows by span and currently lacks an equivalent removal path. Request rollback makes this a recurring lifecycle event, so prune rows whose source spans are no longer in the turn, plus group identities made unreachable by removal. Dispose subscriptions through the existing ownership path and avoid double-disposing spans already handled by the source pipeline.

Do not rebuild surviving rows or reset the whole message to perform rollback. Infinite retry must not retain removed spans through row caches, subscriptions, closures, or temporary activities.

## 2. KernelMixin streaming surface

Expose an instance method on KernelMixin, implemented in a partial file or a narrowly owned executor, with request state local to each enumeration:

```csharp
public IAsyncEnumerable<ChatRequestUpdate> StreamAsync(
    ChatHistory history,
    PromptExecutionSettings executionSettings,
    Kernel? kernel = null,
    ChatRequestOptions? options = null,
    CancellationToken cancellationToken = default);
```

The call-local option initially needs only a nullable maximum-retry override; omitted means the immutable default captured from Assistant. Do not introduce endpoint fallback, arbitrary execution delegates, or a public policy/plugin registry without a production requirement. Snapshot effective options/settings before the first attempt.

Use a small typed update family rather than a content stream that silently restarts:

| Update | Data | Meaning |
| --- | --- | --- |
| `AttemptStarted` | One-based attempt number | A new SDK request is about to begin; initialize per-attempt state/statistics |
| `Content` | Existing `StreamingChatMessageContent` | A delta from that attempt; no second content schema |
| `AttemptFailed` | Failure record and nullable retry delay | This attempt ended; non-null delay means retry scheduled, null means terminal failure |
| `Completed` | Normalized response completion information | Enumeration ended without a request error; callers still validate business output |

A typed record family keeps consumers exhaustive and avoids a mutable event bag with unrelated nullable fields. Concrete representation should remain small; no extra observables or concurrent queues are needed. Normal delta consumption and lifecycle events are ordered on the same asynchronous enumeration.

Example consumer shape (schematic, not a production implementation):

```csharp
await foreach (var update in kernelMixin.StreamAsync(
                   history, settings, kernel, cancellationToken: cancellationToken))
{
    switch (update)
    {
        case ChatRequestUpdate.AttemptStarted started:
            await BeginAttemptAsync(started.AttemptNumber);
            break;
        case ChatRequestUpdate.Content content:
            await ConsumeContentAsync(content.Value);
            break;
        case ChatRequestUpdate.AttemptFailed failed:
            await FinishFailedAttemptAsync(failed);
            if (failed.RetryDelay is { } delay)
            {
                ResetAttemptOutput();
                await ShowRetryAsync(failed.Failure, delay);
            }
            break;
        case ChatRequestUpdate.Completed completed:
            await FinishCompletedAttemptAsync(completed);
            break;
    }
}
```

The example's methods represent existing consumer work and proposed local factoring, not required interfaces or callback parameters.

### Event and exception contract

For two failures followed by success:

```text
AttemptStarted(1) -> Content* -> AttemptFailed(retry delay)
  await delay
AttemptStarted(2) -> Content* -> AttemptFailed(retry delay)
  await delay
AttemptStarted(3) -> Content* -> Completed
```

For terminal request failure, emit `AttemptFailed` with no retry delay, then throw the normalized terminal exception on continued enumeration. The failure update finalizes statistics; the existing outer exception path creates the error row once. It must not create a second error row in the update handler. Resolve the terminal cause once; a late cancellation must not overwrite an already-selected unrelated terminal failure.

Cooperative cancellation throws cancellation directly, without an `AttemptFailed` error record. The consumer's `finally` closes any still-open attempt accounting and activity. Breaking enumeration (for example, a successful connectivity probe) disposes the active stream without starting another attempt or manufacturing a failed response. Success, terminal failure, consumer abandonment and cancellation all release request-local state.

The executor catches failures from obtaining/enumerating the SDK stream and interpreting provider protocol errors, not failures thrown by the consumer after an update was yielded. UI, persistence, statistics, and tool execution are outside this catch boundary. Keep SDK enumeration in a narrow helper/manual-enumerator boundary; C# async iterators cannot simply place `yield return` inside a `try` with `catch`. Disposal must close the SDK stream, not start recovery when a consumer is leaving.

### Stable input between attempts

Keep the caller's canonical history untouched. The existing approval path already uses `new ChatHistory(history)` because adapters may append to their input history. Apply a fresh per-attempt history container from the original logical-request snapshot. Do not reuse the mutated container from the previous attempt or rebuild it from provisional chat output.

A shallow container copy addresses known append behavior. Treat content/settings as read-only and audit current adapters for nested mutation rather than serializing everything through JSON or assuming a shallow copy protects arbitrary nested writes. No new deep-copy framework is justified without such a mutation.

### Completion and response validation

`Completed` distinguishes transport completion from business validity. Carry normalized completion information such as normal stop, output limit, filtering, or unknown compatible-provider termination. Explicit provider error events become classified failures; protocol-specific interpretation remains in the provider/request boundary.

The main consumer must validate completion before building/committing an executable tool batch. The reviewer rejects an incomplete approval decision; compression requires a usable summary without tool calls; topic generation requires usable text. These failures do not automatically become transport retries. A missing finish field alone is not failure, and a tool-only answer is not an empty response.

## 3. Classification and terminal diagnostics

Refactor `HandledChatException.Handle` internally into evidence extraction and classification, retaining its external role and applying the documented heuristics. Request context supplies cancellation/timeout evidence; localized strings do not control policy. A classification record carries the semantic category, status/code, rule/evidence source, request ID and server delay when available.

One failure record identifies an attempt, its time and normalized exception/evidence. Keep a last-10 queue inside the logical request and a total failure count. Do not store prior queues inside each record. The terminal wrapper can be a `ChatRequestException : HandledChatException` with bounded attempt records and total count, using the final failure's category/status as its primary semantics. This preserves existing `HandledChatException` checks for context overflow and avoids having callers inspect an AggregateException just to choose compression recovery.

The final wrapper's ordinary InnerException points to the final cause; its attempt records give the dialog the earlier retained causes. The same exception object may be referenced rather than copied. Single-attempt failure uses the same external contract. On success/cancellation, release temporary records instead of attaching them to the message. Existing usage/statistics remain; clearing conversational diagnostics does not mean rewriting historical accounting.

For the assistant message, prefer a runtime-only `Exception? Error` plus the existing persisted `ErrorMessageKey`. This can also hold unrelated generation exceptions. The dialog understands a request wrapper's bounded records and ordinary exception chains; no production constructor or custom failure hook should exist solely for tests.

`HandledChatException.Handle` must preserve already-normalized terminal wrappers. Error details must not be reformatted/flattened repeatedly by each layer. Fail-closed approval can turn the final request failure into concise tool feedback as it does today; this is the tool's result contract, not an injected network-error message to the outer conversation.

## 4. Activity scope and UI ownership

Add `RequestRetryActivityItemPresentationRow : ActivityItemPresentationRow`, using `ActivityMarker` with a localized Header and SecondaryHeader. Do not subclass the reasoning row or store retry text in reasoning spans.

Use the existing `ChatContext.SetBusyActivityAsync`/ChatPresentation dispatcher boundary as the pattern, but a request retry needs an updateable scope and explicit owner. A small request-activity scope can expose an awaited update and asynchronous disposal. This is a dedicated production lifetime, not a general activity event bus.

- Allocate the scope/row lazily on the first retry, not once per content delta.
- Capture the actual owning message/tool/compression node and position explicitly. The current generic busy activity infers the last assistant node; that is insufficient for manual compression and nested requests.
- Keep one stable row for the logical request; update count and friendly key on the UI dispatcher. The row should not hold raw exceptions or the diagnostics queue.
- Use awaited updates so a disposed scope cannot be recreated by a delayed `IProgress<T>` callback. Clear/detach in the consumer's `finally`, including cancellation before/during delay.
- Generalize the existing temporary-activity collection only enough to carry this second activity type and its anchor. Do not create a separate competing turn projection or put a global current-retry property on GenerationContext.
- A retry activity joins its owner's activity group. Manual compression attaches to its compression presentation, while approval remains associated with its tool; detached title generation has no visible retry row. None should make a completed unrelated assistant node appear busy.
- Preserve row/group identity during updates and prune cache entries when the temporary source is detached. Suppress duplicate generic pending state while retrying.

On success, remove the row without leaving a completed retry item. On terminal failure, remove it before presenting the normal friendly error/details. On cancellation, remove it before presenting the persisted stopped state. Intermediate failure records are never serialized through the row.

## 5. Persisted cancellation and propagation

Add observable `IsCanceled` to AssistantChatMessage with a new unused MessagePack key and default false. Keep `Error` runtime-only with explicit MessagePack/JSON ignores. Do not add a persisted multi-state generation enum merely to encode the already existing `IsBusy`, `ErrorMessageKey`, and completion timestamps.

`AssistantCanceledPresentationRow` projects the marker. It participates in terminal and historical process presentation, with neutral `已停止` text and the normal Continue action only for an eligible latest stopped node. Its creation does not depend on an exception surviving in memory. The branch projector must handle this before the successful-empty branch so a canceled empty answer is not also "no response".

At `GenerateAsync`, handle cooperative caller cancellation before the broad exception handler. Mark canceled and let normal finally blocks close spans, clear busy state, dispose GenerationContext, and record canceled usage. Do not set ErrorMessageKey or Error for ordinary caller cancellation. Existing genuine errors are not reclassified solely because the token became canceled later.

### Compression needs its own propagation fix

Current `CompactContextAsync` catches any OperationCanceledException, calls `compressionMessage.Fail(...)`, and returns false. Some callers then return rather than propagate cancellation. Changing only GenerateAsync's catch is insufficient.

Give ContextCompressionChatMessage an explicit canceled completion as well, because manual compression can run without an assistant message. Its existing persisted action node owns that outcome; do not create a fake assistant message. Its header/visibility can represent canceled work without an ErrorMessageKey or successful summary.

- Compression marks itself canceled and rethrows cooperative cancellation to its actual operation owner.
- Automatic compression cancellation propagates to GenerateAsync so the assistant turn receives the stopped marker; the compression row has neutral canceled status and no duplicate error banner.
- Manual compression handles cancellation at its manual entry point, finalizing its existing node without generating an assistant error message or routing expected cancellation to the generic error handler.
- Update compression visibility and `NeedsAutomaticCompaction` deliberately: a canceled summary never replaces history, and the absence of ErrorMessageKey must not accidentally treat an unresolved context-overflow recovery as complete. A later Continue still evaluates context usage/overflow normally. Preserve existing failed-compression retry semantics independently of the new canceled state.

The reviewer already rethrows OperationCanceledException when its caller token is canceled; preserve this boundary. Tool invocation currently catches exceptions into FunctionResultContent: retain the result-before-cancellation behavior and required pairing, but avoid error-level logging and red exception details for expected caller cancellation. Cancellation still reaches the outer generation once the tool result is stored. A canceled subagent contributes the appropriate tool result; it does not independently mark a still-running parent as stopped unless the parent's own operation was canceled.

## 6. Statistics and caller integration

The request executor owns retry scheduling; ChatService remains the owner of application statistics and parent/turn/message linkage. Use AttemptStarted to create one invocation record per actual SDK request and AttemptFailed/Completed to close it once. The consumer's finally closes an attempt interrupted by cancellation or a local consumer failure. Restore ambient invocation IDs using the existing ownership pattern.

Keep fresh ChatUsageDetails per attempt and accumulate reported usage into the message once at attempt end, including failed attempts. Do not use its additive `Accumulate` as a way to merge cumulative deltas from repeated attempts into one max-value accumulator. Missing usage is unknown, not a fabricated zero-cost request. Return only the accepted attempt's usage for context-usage/compression decisions; total accounting includes all attempts.

| Caller | Reset on replay | Work after accepted response | Retry UI owner |
| --- | --- | --- | --- |
| Main chat | Attempt span suffix, message metadata, tool builder, attempt usage/timing | Commit tool calls, invoke batch, then next logical request | Current assistant message |
| Approval | Reconstructed response segments/metadata and tool builder | Execute reviewer tools, update read/correction budgets | Pending tool activity |
| Compression | Summary builder and tool builder | Validate/commit summary; trim input only through existing context-recovery logic | Compression node |
| Topic | Title builder | Normalize and commit title | None |
| Connectivity | Probe state | Stop enumeration once the existing probe criterion is met | Existing connectivity surface, normally no retry |

The approval request already reconstructs a full response from streamed content. Extract one shared internal streaming response reader for approval/compression/topic, while keeping their validation and tool loops outside it. It returns the reconstructed response, accepted-attempt usage, and normalized completion. It owns fresh reconstruction per attempt and uses the same request accounting as the visible-chat consumer. Keep the visible chat consumer distinct because it incrementally writes spans. Share lifecycle/accounting through a small ChatService-owned attempt scope; neither duplicate it in every request path nor catch arbitrary consumer exceptions as request failures. The reader is a streamed implementation returning an assembled result, not a non-streaming SDK call.

Factory construction captures RequestMaxRetries next to the current timeout snapshot. Keep policy data separate from credentials/endpoint data rather than turning ModelConnection into mutable request state. [SDKReview.md](SDKReview.md) verifies that the current OpenAI/System.ClientModel path retries three times and Anthropic retries twice; disable their transient retry policies explicitly. AttemptStarted counts an application-controlled SDK request attempt; the official authentication handler may still refresh credentials and resend once within it, so this is not a physical HTTP-send counter. The design does not silently redefine the existing 20-second timeout as a whole-stream deadline. Initial and post-header timeout behavior differ across these exact SDK versions and need an explicit contract before implementation. Ollama's pending line read and connector error-body reads also need cancellation fixes at their real transport/adapter boundaries.

Use the repository's existing dependency patch mechanisms for these confirmed defects: a shared source replacement in the Google/Mistral mirror projects, and a narrow static IL donor patch for OllamaSharp. Remove the fabricated HTTP 400 at the connector boundary, preserve cooperative cancellation, and recognize Ollama chat protocol errors before mapping updates. These are part of the coordinated refactor, not permanent compatibility branches in ChatService or its retry executor. SDKReview.md records delivery boundaries and validation against the patched runtime assemblies. SDK settings that already expose the required behavior remain configuration changes.

## 7. Refactor existing operation owners together

There are three actual ownership levels:

```text
ChatContext operation: busy state, cancellation, awaited lifetime
  Generation or explicit compression: conversation/tool/summary decisions
    Logical request: streamed attempts, classification, retry
```

Do not implement all three as generic workflow/state-machine frameworks. Their concrete production roles already exist; clarify their contracts and remove duplicated owners.

### Awaitable context operation

Current ChatContext.TryExecute is a UI-thread entry point that owns IsBusy, BusyChatContexts, the local cancellation source, Task.Run, and terminal error dispatch. Current EssentialPlugin.RunSubagentAsync bypasses it and awaits GenerateAsync directly with the parent's token. It also creates its initial AssistantChatMessage without the busy initialization used by ordinary chat entry points. These paths should not keep different lifecycle semantics after the refactor.

Provide one awaitable context-operation core used by both UI-started work and tool-started subagents. It owns acquiring/releasing the busy state and links parent cancellation to the child's local cancellation source. The existing immediate UI entry points may remain thin command/scheduling wrappers, but must delegate to this same owner; do not nest two owners around one generation or wrap a detached operation in polling/TaskCompletionSource just to make subagents awaitable.

Acquire/release UI-affine state on the dispatcher. Preserve one operation per ChatContext without adding locks to UI-owned state. The child operation's task represents actual completion, failure or cancellation. Parent cancellation reaches the child; canceling a child does not cancel its parent token. Retry loops remain below this operation lifetime, so backoff keeps the correct context busy and cancelable.

GenerationContext continues to own generation resources, approval state and ambient function context. Its lifetime stays inside the operation and is cleared/disposed once. A child retains its independent GenerationContext and the existing shared approval-state policy. Do not move request attempt counters or a single current-retry slot into GenerationContext.

### Generation result is an explicit contract

Current GenerateAsync catches errors into presentation and returns Task without a result. RunSubagentAsync then examines only the initially allocated assistant message and its last span. GenerateAsync can replace its local active message after compression, so this reference is not a reliable final-result contract. The source already contains a TODO about returning structured subagent errors.

Change the generation contract to return a typed result containing the actual final assistant message/output and any terminal failure. Normal failure is presented once by its owning generation and remains inspectable by a tool caller; it must not be mistaken for successful partial text. A cooperative cancellation still propagates through task cancellation after marking the owning message stopped, consistent with C# async conventions. Do not require callers to parse localized errors, scan arbitrary last spans, or infer success from Count > 0.

This runtime result does not require another persisted outcome enum. Existing persisted friendly error/cancellation fields serve presentation; the runtime result serves callers and can identify a terminal compression failure even if its detailed UI belongs to the compression action. Preserve the actual normalized cause and useful partial output without duplicating an error banner at every layer.

Main UI entry points consume the same operation/result contract as subagents. Retry still creates the requested branch, Continue still appends, and these graph operations remain outside the request executor. Update IChatService and every caller together rather than retaining a compatibility overload that no production caller needs. Its current Continue XML documentation incorrectly says it creates a branch; update it with the actual append behavior.

### Subagent result and cancellation

RunSubagentAsync resolves the assistant and creates child context/presentation as today, then awaits the shared context-operation/generation contract. It returns actual completed output, or an explicit structured tool failure carrying concise failure information and useful partial output. Use the existing tool-result infrastructure for this; do not introduce a second subagent-only exception protocol or return failed partial output as apparent success.

The parent's function-call batch still receives exactly one result for that run_subagent call. Internal request retries do not re-run the entire child generation. Local child cancellation becomes a canceled tool outcome if the parent remains active; parent cancellation stores the available tool outcome before unwinding the parent's operation. Preserve the existing ambient function-context suppression around child initialization/execution.

No approval policy, request classifier, or generic retry executor should special-case the function name run_subagent. The structured failure is a tool implementation's normal result, and the child's retries remain transparent to the outer LLM until a terminal tool outcome exists.

### Compression owns recovery, not request mechanics

Refactor RequestCompressionSummaryAsync onto the shared streamed response reader, removing its separate SDK loop/accounting. Compression's business recovery handles only input changes: a context-overflow result can trim the oldest complete conversation unit and issue a new logical request. Network errors retry the unchanged input below that boundary and never trigger trimming. Summary validation remains in compression, and only a validated summary changes the effective context checkpoint.

Replace ambiguous bool-based compression outcomes with explicit business outcomes at its callers: applied summary or terminal failure; cancellation propagates separately. Automatic threshold compression, context-overflow recovery and manual compression can then choose their actual policies without inferring cancellation from false. Preserve successful coverage boundaries and previously committed context. Remove the old cancellation-to-Fail-to-false path, rather than adding checks after every existing return.

### Approval, topics and connectivity

Move approval's request reconstruction onto the shared streamed reader and delete its request-local finish-reason string list once normalized completion is available. Keep reviewer decision/read validation and correction counters in the reviewer. The normalized request failure can still map to ToolApprovalFailure and a concise tool result; eliminate duplicate classification of the same failure.

Move topic generation onto that reader as well. Keep title postprocessing, assistant selection and detached UI behavior local, but remove its independent SDK catch/stream/statistics lifecycle. Expected cancellation does not log as title-generation failure.

Route connectivity through KernelMixin.StreamAsync with its deliberate no-retry/early-disposal policy. Provider IChatCompletionService or IChatClient implementations remain transport adapters; their SDK-required interface methods are not additional application generation paths. All application request callers use the shared streaming entry point.

### Superseded paths to remove

| Current responsibility duplicated or ambiguous | Intended replacement |
| --- | --- |
| Direct SDK stream loops in main chat, compression, approval and topic generation | KernelMixin request executor; two consumers: visible spans and shared assembled response |
| Each caller's attempt usage/timing/invocation bookkeeping | Shared ChatService-owned attempt accounting scope |
| Approval-only HasIncompleteFinishReason string checks | Normalized provider completion plus reviewer business validation |
| Message-first parser chain that can stop before HTTP fallback | Evidence extraction followed by one deterministic classifier |
| GenerateAsync swallowing outcome; subagent reading initial message's last span | Explicit generation result and shared awaitable context operation |
| Compression converting cancellation to an error and false | Persisted canceled completion, cancellation propagation and explicit compression result |
| Cooperative cancellation reaching broad friendly-error handlers | Operation-owned stopped completion; ordinary error path for actual failures |
| Per-request SDK default retries plus application retries | One controlled retry budget after provider adapter reconciliation |

This is the scope of the coordinated refactor. It does not authorize unrelated plugin changes or replacing the existing chat tree, tool protocol, compression algorithm, statistics database, or presentation system. A migration may be implemented incrementally, but a half-migrated request path is not the completed design.

## 8. Focused validation scenarios

These are intended behavioral checks, not tests run during this design pass:

1. A successful tool batch followed by two failed requests and success executes the tools once, preserves their results, discards failed output, and leaves no historical retry/error row.
2. A stream outputs text/reasoning/metadata, fails, and then succeeds: neither UI, serialized history, nor later Continue contains superseded content; output-row caches release removed spans.
3. All attempts fail: the final error exposes every retained failure up to ten, counters describe any omitted records, and useful final partial output remains available to Continue.
4. Repeated failures with `-1` keep one retry row, bounded diagnostics, no growing empty-message history, and remain immediately cancelable.
5. Cancel before first output, during content, during a tool batch, during retry delay, and during manual/automatic compression: persist stopped state, maintain call/result pairing, suppress cancellation error banners and no-response rows, and keep Continue functional where applicable.
6. A consumer/storage/statistics failure does not trigger another API request. Early enumerator disposal also does not trigger retry.
7. An SDK appends to request history before failure: the next attempt still receives the original logical input without extra calls/results.
8. Approval network retries consume neither file-read batches nor missing-decision correction attempts. After accepted reviewer-tool execution, the next request preserves those tool results.
9. A subagent performs tools, compresses context, and returns: the parent receives the actual final output rather than the original message's last span. Terminal child failure is a tool failure with partial output, not an apparent success.
10. Parent and child busy/stop behavior follows the same operation contract: parent stop cancels child, child-only stop returns a canceled tool outcome without stopping an otherwise active parent, and cleanup leaves no stale busy contexts.

Classification fixtures remain in ErrorClassification.md. SDK defaults and source-level timeout/retry mechanisms are now verified in SDKReview.md; native UI behavior, persistence races, stalled-read cancellation and integration against a real/mock endpoint remain unverified. No runtime code, application dependency, or test server is introduced by this document.
