# Tool Approval Architecture and Technical Design

Updated: 2026-10-02.

This document describes the current architecture and product contracts. Source code is authoritative for implementation details. Verification boundaries are collected in section 11.

## 1. Modes and boundaries

| Mode | Behavior when approval is required |
| --- | --- |
| `Ask` | Existing manual approval interface and remember/custom options. This is the default. |
| `Auto` | An approval assistant decides the pending scope. Denial or failure blocks it and returns an explanation to the executing assistant, without human fallback. |
| `FullAccess` | Bypass outer and tool-internal approval gates. Present this as a dangerous option. |

Auto is a best-effort second check against unintended destructive actions, excessive scope, and other high-impact mistakes. It trades tokens and latency for scrutiny; it does not guarantee correct classification, complete script analysis, or prompt-injection prevention.

FullAccess changes approval only. Disabled tools, OS privileges, argument validation, filesystem correctness checks, and capability limitations remain enforced. Ordinary `AskQuestionAsync` interactions are not approval gates.

There is no provider-specific forced tool selection, prose/JSON decision parser, ordinary subagent review conversation, new permission sandbox, or persistent per-chat mode override.

## 2. Ownership and generation lifetime

| Owner | State and responsibility |
| --- | --- |
| [PluginSettings](../../src/Everywhere.Core/Configuration/Settings/PluginSettings.cs) | Persisted global `ApprovalMode`, representing the desired mode for newly started turns. |
| [ChatContext](../../src/Everywhere.Core/Chat/ChatContext.cs) | Observable, non-serialized `GenerationContext`; children also retain a runtime `InheritedApprovalState`. |
| [GenerationContext](../../src/Everywhere.Core/Chat/GenerationContext.cs) | Generation kernel, executing configuration/connection, rendered constraints, context settings, and ambient invocation scopes. |
| `GenerationApprovalState` | Observable mode state shared by a turn and ordinary children. Volatile reads and atomic writes support worker invocation entry and UI switching. |
| [FunctionCallContext](../../src/Everywhere.Core/Chat/FunctionCallContext.cs) | Immutable invocation mode snapshot, consent routing, and denial/failure feedback. |
| [PersistentState](../../src/Everywhere.Core/Configuration/PersistentState.cs) | Local first-use acknowledgements, outside cloud-synchronized settings. |
| [ChatWindowViewModel.ToolApproval.cs](../../src/Everywhere.Core/ViewModels/ChatWindowViewModel.ToolApproval.cs) | UI projection, confirmation/selection, immediate switching, and session warning dismissal. |

`CreateGenerationContextAsync` captures the global mode before asynchronous initialization, unless the chat inherits a same-turn approval state. ChatService publishes the initialized context and clears it in the owning generation's `finally` path before disposing the mixin. Creation failure disposes resources without publishing stale state.

A turn is the complete `GenerateAsync` request/tool loop, including continuations and automatic compaction, rather than one provider response. New user turns, regenerate, and continuations that start a new generation capture the current setting. Manual compaction has `IsConversationTurn = false` and does not appear as a tool-executing turn in the approval UI.

`AsyncLocal<FunctionCallContext?>`, `EnterFunctionCallContext`, and `SuppressFunctionCallContext` belong to GenerationContext and restore previous values on scope exit. Separate chats and nested generations can overlap; singleton ChatService does not imply single-threaded execution. Children share mode state, not kernels, histories, provider resources, or ambient invocation slots.

### Mode transitions

| Event | Saved global mode | Effective running mode |
| --- | --- | --- |
| Select while idle | Updated after any first-use confirmation. | No active turn; display the saved selection. |
| Select a different mode while running | Updated. | Unchanged; show the current/next notice. |
| Visit a running background chat | Unchanged. | Preserve its captured mode and re-evaluate the notice. |
| Click Switch now | Unchanged. | Update the displayed turn's shared state, including ordinary children. |
| Turn ends | Unchanged. | Clear runtime context; subsequent turns use the saved selection. |

Pending state is the inequality between running and saved modes; there is no separate pending field. Switch now resolves the displayed generation at execution time instead of retaining a previously displayed chat reference.

Invocations not yet entered capture immediate changes even if their calls were emitted earlier. An entered invocation retains its snapshot for later internal gates. Existing manual cards, active reviews, and execution do not change modes midway. Global selection never silently grants access to background turns.

## 3. Two approval layers

The outer gate in `ChatService.InvokeFunctionAsync` handles general plugin/function approval, including MCP tools. Built-in `OnPermissionConsent` callbacks can defer approval to a specific gate inside the tool.

Tools resolve operations and decide when internal consent is needed: final paths, affected files, shell commands, and UI action batches. Shared `RequestConsentAsync` chooses how consent is obtained, using the invocation snapshot at both layers.

Auto preserves existing global/session approvals, approved paths, and general plugin/function bypass rules. It replaces only consent that would otherwise show a manual card. A false bypass rule requires approval; it does not deny execution. Automatic approval is allow-once and never creates remembered, session, path, or MCP-server rules.

FullAccess bypasses both gates, including ID-specific and forced consent. Tool bodies and necessary validation still run. Approval callbacks must not be the sole location of non-approval correctness checks.

Each required consent is reviewed independently. An outer allow does not authorize later internal operations, and there is no cache by tool name. Partial-result accounting remains authoritative if later consent is denied after earlier effects. Filesystem patch review gathers decisions before committing accepted operations; it does not commit each file immediately after review.

`TerminalPluginSettings.BypassesApproval`, its settings entry, setter warning, and dedicated branch are removed. The forward migration discards both `BypassesApproval` and historical `AutoApprove`, without deriving a mode or generic permission. `ShellPath` and independently established generic rules survive.

## 4. Approval assistant and private conversation

[SystemAssistantSettings.ToolApproval](../../src/Everywhere.Core/Configuration/Settings/SystemAssistantSettings.cs) uses `ModelSpecializations.ToolApproval`. AutoSelect reuses the executing generation's configuration and connection, including background tasks, rather than foreground UI selection or catalog substitution. Manual selection gets a separately owned mixin, disposed after review.

The private nested `ToolApprovalReviewer` in [ChatService.ToolApproval.cs](../../src/Everywhere.Core/Chat/ChatService.ToolApproval.cs) owns one consent review: private history, kernel, streaming requests, statistics, and bounded dispatch loop. It directly reuses the owner's helpers, without a request delegate or general agent executor.

Its kernel registers only `read_file` and `submit_approval`. Review does not call normal `GenerateAsync`, construct a subagent ChatContext, or load executing-assistant plugins/MCP servers. The executing prompt is supplied as constraints; the built-in reviewer system prompt governs review.

Private messages, reasoning, and file results are transient and excluded from main-chat history. Only outcome and concise explanation return through normal tool results. Owning-operation cancellation propagates as cancellation, not policy denial.

## 5. Input snapshot and semantic scope

`BuildApprovalInput` uses explicit types in [ToolApprovalInput.cs](../../src/Everywhere.Core/Chat/Permissions/ToolApprovalInput.cs) with `ToolApprovalInputJsonSerializerContext.ForPrompt`. Arguments retain JSON structure; UI display trees are not serialized.

| JSON section | Current contents |
| --- | --- |
| `ENVIRONMENT` | `WorkingDirectory`; no fabricated shell environment or repository provenance. |
| `EXECUTION_CONSTRAINTS` | Generation's rendered system prompt as a string. |
| `EFFECTIVE_HISTORY` | Ordered `user-instruction`, `checkpoint`, and `fact` entries: user/strategy content, summary, attachment facts, and prior call arguments. |
| `PENDING_ACTION` | `PluginKey`, `ToolName`, `Description`, `Parameters` (name, description, requirement, schema, default), and exact `Arguments`. |
| `CONSENT_SCOPE` | Optional `Operation` and JSON `Details` from the specific gate. |

`ChatHistoryBuilder.SelectContextMessages` uses the executing generation's `MaxContextRounds` and context limit. Review does not reselect smaller history against its own capacity. The retained compression summary is current context; discarded history is not reconstructed or explained again.

Historical calls require a recorded result, excluding the pending invocation and unstarted siblings. Projection retains names/arguments, not bulk outputs or assistant prose/reasoning. A result alone does not prove success or authorization. Attachments use existing paths, descriptions, selected text, or headers; there is no new screenshot/OCR or ingestion pipeline.

Input is frozen before requests. Arguments containing `prompt`, including delegation calls, are action data. There is no `run_subagent` exception or reconstruction of its internal ChatMessage conversion.

`ToolApprovalScope.Create<T>(operation, details, JsonTypeInfo<T>?)` captures a JSON snapshot. Built-in callers supply source-generated metadata; omission uses reflection serialization. Other tools can supply their own types and metadata through the optional consent parameter.

| Gate | Semantic details currently supplied |
| --- | --- |
| Outer tool gate | Operation `Tool invocation`; definition and arguments already appear in the input. |
| Filesystem | `ToolApprovalFileScope`: resolved paths and explicit-approval requirement. |
| Terminal | `ToolApprovalShellScope`: selected shell path/type, command, and explanation. |
| Visual actions | `ToolApprovalUiScope`: explanation; original actions remain in pending arguments. |
| macOS SystemPlugin | Original definition/arguments; no additional semantic scope currently supplied. See section 11. |

`submit_approval` takes no replacement operation, target ID, or arguments. Its decision applies only to the pending scope. Conditions in a reason cannot narrow execution; changing an operation requires denying the submitted one.

## 6. Streaming and host-controlled dispatch

Approval uses `KernelMixin.StreamRequestAsync` through ChatService's shared response reader, ordinary automatic tool selection, and `autoInvoke: false`. There are no provider-specific required/named-tool controls.

Shared `FunctionCallContentBuilder` assembles calls; text, reasoning, and continuation metadata are reconstructed into private history. Explicit unsuccessful termination uses the shared request exception pipeline. Before a decision, it cannot authorize execution. Successful execution of `submit_approval` ends review: subsequent calls, text, and provider errors are ignored.

Where the SDK supplies an explicitly completed function call, the reader can dispatch the accumulated calls during streaming and dispose the stream after submission. Valid-looking JSON fragments alone do not establish completion. Other calls are dispatched after successful enumeration exhaustion.

Each request receives a history copy because adapters may append calls to their input collection. Only the host records the reconstructed response in canonical history, preventing duplicate calls.

Plugin metadata is cleared at registration to advertise bare names. The shared builder follows ChatService's flat-name contract. The approval loop has no separate plugin/function-name validator or handwritten router.

The host resets the current-response read marker and invokes calls sequentially through `FunctionCallContent.InvokeAsync`; SK handles lookup, parsing exceptions, binding, and invocation. Missing/duplicate IDs, unknown calls, invocation errors, or a fifth read batch fail review if encountered before submission. Reads in a response share one batch.

The first successfully executed submission returns its typed `ToolApprovalResult` immediately. Reads before it may finish; calls after it are ignored. Invalid decision arguments fail review. The host does not defer a submitted decision or require a closing-message request. Continuations without a decision use `FunctionResultContent.ToChatMessage` for result history.

## 7. Tools and bounded cost

| Tool | Contract |
| --- | --- |
| `read_file` | `path`, `offset = 1`, `limit = 2000`; bounded evidence and continuation metadata. |
| `submit_approval` | Exactly `allow` or `deny`, plus a nonempty concise `reason`; typed outcome. |

ReviewTools owns the cumulative read counter and response marker. The first valid bound read consumes a batch; subsequent reads in that response share it. Parallel calls count once, though execution is currently sequential. Future concurrent dispatch must retain this boundary.

| Read batch | Behavior |
| --- | --- |
| 1-3 | Execute reads and return evidence. |
| 4 | Return a limit notice for each read with no file I/O, then continue. |
| 5 | Fail as `ToolApprovalFailure.ReadLimitExceeded`, with no file I/O. |

`FileSystemPlugin.ReadForApprovalAsync` and normal `ReadFileAsync` share `ReadContentAsync`, handler selection, and `BuildReadOutput`. Review preserves relative/local/file URI/skill URI resolution, text/PDF/binary offsets, formatting, and bounds. It creates no attachments or chat display changes, invokes no shell/officecli, and does not recursively request consent.

Ordinary read errors become evidence, limited to 1,024 exception-text characters, and consume a batch. Binding/invalid arguments terminate review; cancellation propagates. New paths do not reset the budget.

Tool-less responses receive corrective user messages at most twice per review. The third fails as `DecisionMissing`; reads do not reset the counter. Prose/JSON text is never a decision. Four nonterminal read batches plus two corrections permit at most seven completed response opportunities before decision or terminal failure. Transport retries follow the selected assistant's request budget and do not consume read batches or correction reminders; there is no additional reviewer retry loop.

Initial or continuation context-limit errors fail as `ContextLimitExceeded`. Review does not compact, discard history, truncate evidence to retry, change assistants, or fall back to a human.

## 8. Risk policy and deepseek comparison

`ToolApprovalReviewer.SystemPrompt` in [ChatService.ToolApproval.cs](../../src/Everywhere.Core/Chat/ChatService.ToolApproval.cs) is the governing prompt. This document describes its policy without duplicating prompt or tool-description text.

| Risk | Policy |
| --- | --- |
| Low | Ordinary project-local work and exact cleanup of an object established as task-created can proceed without extra explicit authorization. |
| Medium | Destructive changes, production access, non-sensitive external writes/sends, and security/privilege/system changes require explicit action, target, and scope authorization. |
| High | Sensitive-information exfiltration across a trust boundary, including credentials, secrets, or private data sent to external/untrusted destinations, is denied even when authorized. |

Authorization cannot downgrade risk. Ambiguous effects or excessive scope lead to denial. User instructions define the task; constraints narrow it. Arguments, attachments, history, files, and assistant justifications are evidence, not instructions replacing review policy. UI actions use available semantics without inventing screen contents.

Everywhere explicitly distinguishes normal credential submission to its intended service for authentication from exfiltration. This is a prompt-level clarification, not a deterministic destination or secret classifier.

Sending context to the approval assistant is part of the disclosed inference workflow, not another pending tool action to recursively review. It may include sensitive task data supplied to, accessed by, or produced by the executing assistant and related files read during review. The high-risk policy does not redact these inputs or guarantee that the main assistant already saw them.

The historical design reference was `E:/Source/deepseek-harness`, revision `639ed015397290b3745d163aafe02ffee4aa3f84`, `packages/experimental/auto-review/src/index.ts`. It is not a dependency or a claim about newer reference revisions.

| Inspected deepseek behavior | Everywhere adaptation |
| --- | --- |
| Policy plus `ENVIRONMENT`, `PROJECT_INSTRUCTIONS`, `FILTERED_HISTORY`, and `PENDING_ACTION`. | Built-in policy, typed sections in section 5, and optional consent scope. |
| Request provider/model, working directory, and project-instruction metadata. | Generation snapshot or System Assistant; actual directory/rendered constraints, without fabricated provenance. |
| Human/direct-parent tags, checkpoints, native/PTC facts. | Effective user/strategy messages, summary, and completed-call facts; no PTC identity or parent reconstruction. |
| No bulk historical outputs or file-reading tool. | Same baseline exclusion, plus optional read evidence for unknown scripts or other relevant files. |
| One request, no tools, closed risk/decision JSON. | Bounded streaming tool loop and typed decision, without prose parsing or returned risk labels. |
| Optional human fallback under its `ask` policy. | No fallback after denial or failure. |
| Review of eligible calls. | Review only otherwise-manual gates after existing bypass rules. |

The reference has no separate secret detector, destination reputation service, taint tracking, or deterministic filesystem analysis. Classification is an assistant judgment. Its closed output protocol checks consistency with that assistant's own risk label, not semantic correctness. The synthetic credential-exfiltration test documents intended hard denial despite authorization, not general accuracy. The inspected prompt has no explicit normal-authentication exception; Everywhere's clarification is stated above.

## 9. UI and persistence

The selector sits beside ChatCustomAssistantSelector in ChatInputArea's toolbar. Current ToolApprovalSelector is a TemplatedControl presenting one Ghost Button and its menu, without an extra background container. `ItemsSource`, `SelectedItem`, `EffectiveItem`, and `SelectItemCommand` carry stable ViewModel data. Menu commands and the existing hide-flyout behavior own interaction; the control has no settings/chat-service state.

The button displays the effective mode; the menu marks the saved next-turn mode. ChatWindowViewModel uses switched current-chat/generation subscriptions, observes mode/global settings, marshals updates to UI, and disposes subscriptions with its lifetime.

The pending notice immediately above the input explains current and next modes and offers Switch now. It has no dismiss command; equality, turn completion, or immediate switching removes it. Selecting a mismatched running chat shows it again.

Selection commands show independent first-use Auto/FullAccess dialogs through the window-bound DialogHost. Cancel commits neither mode nor acknowledgement. Confirmation stores local-only `PersistentState.HasAcknowledgedAutoApproval` or `HasAcknowledgedFullAccess`. These are workflow notices, not permissions. Checks currently occur on selector-driven changes; restoration is discussed in section 11.

Auto's cards explain who reviews (AutoSelect uses the executing assistant), task context and optional reads sent to that assistant, and denial/failure including sensitive-data policy. A note explains service processing and misjudgment. FullAccess explains files, terminal, connected tools, and data-loss/disclosure/untrusted-content risks with danger styling. Generated localization resources own actual copy; there is no separate dialog-text draft here. UI uses assistant rather than model terminology.

The FullAccess warning appears if either saved or displayed effective mode is FullAccess. Its wording covers current or next turn; the pending notice gives the exact distinction. Singleton ViewModel dismissal lasts the application launch, including toggles/chat switching, and resets on restart. Pending notice remains closest to input when both are visible.

Review uses lightweight invocation activity preview and restores it afterward. It does not enter human-input wait state or create a manual card.

Global mode and System Assistant configuration persist in settings. Missing/unrecognized textual mode values use Ask through the fallback converter. Generations, inherited state, invocation snapshots, histories, budgets, and dismissal are runtime-only. Restart cannot resume a partial review as approved.

## 10. Outcomes and statistics

[ToolApprovalResult](../../src/Everywhere.Core/Chat/Permissions/ToolApprovalResult.cs) distinguishes allow, policy denial, and failure through `IsAllowed`, `Reason`, and optional `ToolApprovalFailure`. Failure blocks the pending scope.

| Failure | Meaning |
| --- | --- |
| `DecisionMissing` | No decision after two corrections. |
| `ReadLimitExceeded` | Fifth read batch attempted. |
| `ContextLimitExceeded` | Selected assistant's context capacity exceeded. |
| `InvalidResponse` | Classified unsuccessful output, invalid calls or IDs before submission, unavailable tool, binding error, or invalid decision. |
| `AssistantUnavailable` | Configuration reports no tool support. |
| `ProviderError` | Other request/configuration/provider exception; classified failures return their concrete friendly explanation to the requesting assistant. |

Cancellation propagates separately. Outer denial can say the tool body did not run; internal denial identifies blocked work and preserves earlier effects. Outcomes return through normal tool results so the assistant can revise/explain rather than automatically ending the conversation.

Each SDK attempt records independent usage/latency through `RequestStatistics` under `StatisticsModelInvocationPurpose.ToolApproval`, linked to existing chat/turn/message identifiers. No new raw prompts, files, or arguments are persisted in metrics. Outer status is Denied for policy refusal, Error for review failure, and Canceled for cancellation. Agent explanations and localized UI keys remain separate.

## 11. Verification boundary and discussion items

Focused tests reuse the existing streaming IChatClient-to-SK adapter and mock responses to cover terminal submission, pre-decision errors, read/correction budgets, and attempt accounting. Existing real-HTTP SDK tests cover output-limit classification and finish-reason preservation. Test payloads are not a guarantee of live-provider classification quality.

The user reported successful live E2E approval before the latest streaming/dispatch refactors. Those refactors have not been tested against a live provider here. Native macOS execution, native dialogs/layout, and classification quality have separate runtime verification boundaries; Windows builds and synthetic responses do not establish them.

| Discussion item | Current boundary and question |
| --- | --- |
| Restored modes and first-use acknowledgement | Selector commands enforce confirmation. Local/cloud settings restoration can select Auto/FullAccess without checking local acknowledgement. Should restoration also require acknowledgement on a device where the workflow has not been confirmed? No such gate is added here. |
| macOS semantic scope | SystemPlugin routes modes and returns denial reasons but supplies no additional resolved consent details. Is original tool input sufficient, or should generated AppleScript/resolved reminder/calendar/note operations be supplied? |

These boundaries do not reopen mode snapshots, read/correction budgets, no-human-fallback behavior, or sensitive-data policy. They are recorded rather than described as implemented.
