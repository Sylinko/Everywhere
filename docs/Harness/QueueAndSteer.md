# Queued Inputs, Mid-Turn Steering, & Detachable Background Tools

> Architectural specification and implemented design on `Everywhere.Harness`, aligned with `RootChatContext`, `ChatService`, `FunctionCallContext`, `ChatPluginUserInterfaceBroker`, and patterns established in `@tanstack/ai-harness`.

## Goals

1. **Allow sending while busy**: Users are never blocked from composing and submitting follow-up inputs while an agent turn is active (except while editing an earlier historical message node).
2. **Dual delivery modes (`Queue` vs `Steer`)**:
   - **Queue** (default in `ChatWindowSettings.BusySubmissionMode`): Run sequentially as the next turn after the active turn completes cleanly.
   - **Steer** (switchable in settings, via composer toggle button, per waiting item, or `Alt+Enter` / `Ctrl+Alt+Enter`): Redirect the active turn at the next safe execution boundary without tearing down conversation invariants or colliding with context compression.
3. **Opt-in backgroundable tools (`SupportsBackgrounding`)**: Long-running, isolated tools (`execute_in_terminal`, `run_subagent`) declare `SupportsBackgrounding = true` (mirroring `SupportsParallelInvoke`) and can detach to background execution via Steer or manual UI click, waking the session automatically via `BackgroundJobNotificationChatMessage` when they finish.
4. **Approval independence**: Human consent / question dialogs (`RootChatContext.SharedUserInterfaceBroker`) and AI `Auto` approval (`ReviewToolApprovalAsync`) remain completely independent of Queue/Steer and Foreground/Background transitions.

---

## 1. Input Admission & Pending Queue (`ChatPendingInputQueue`)

### Preserving `RootChatContext` Single-Foreground Serialization
`RootChatContext` enforces at most one active foreground `Operation` per `ChatContext` so tree mutations, branch choices, and `Idle` persistence never race.

Rather than running concurrent `GenerateAsync` loops on the same `ChatContext` or eagerly inserting unconsumed future user messages into `ChatData`:
- Every `ChatContext` owns a thread-safe `ChatPendingInputQueue PendingInputs`:
  - Staging user items (`ChatPendingInputItem`) outside `ChatData` prevents unconsumed future messages from being polluted by cancellation, failure, or earlier context compression summaries, while allowing UI inspection, `Queue <-> Steer` toggling, withdrawal back into the composer draft, or deletion.
  - Background job wake notifications (`ChatBackgroundJobNotification`) are held in a parallel internal wake lane on `ChatPendingInputQueue`.
- **Default mode & UI persistence**:
  - `ChatWindowSettings.BusySubmissionMode` defaults to `ChatInputDeliveryMode.Queue`.
  - `ChatInputDeliveryMode` (`Queue = 0`, `Steer = 1`) is persisted on `UserChatMessage` (`[Key(6)]`, nullable: `null` for direct idle turns, `Queue`/`Steer` when submitted while busy) so steered and queued turns retain lightweight badges in history.
  - While generation is busy, the composer watermark shows contextual Queue/Steer guidance based on `ChatWindowSettings.BusySubmissionMode` (instead of random tips).
  - Clicking `Cancel()` (`Stop`) calls `PendingInputs.PauseDrain()` before canceling the foreground operation, leaving any remaining waiting items editable in the pending tray with `[Resume]` and `[Clear]`.

---

## 2. Turn Loop Ordering & Steer Join Points (`ChatService`)

### Outer Drain Loop (`DrainPendingWorkAsync`)
`SendMessageAsync`, `EditAsync`, `Retry`, `Continue`, and idle resume (`ResumePendingInputs` / `ForegroundOperationCompleted` / background job wake) all drain waiting work sequentially inside a single continuous foreground operation (`IsBusy` stays seamless across queued turns):
1. Drain completed background job wake notifications (`ExecuteWakeTurnAsync`), appending `BackgroundJobNotificationChatMessage` nodes (`ChatMessageKind.BackgroundJobNotification = 10`, `IsTurnStart() == false` so it never creates spurious Turn Navigator entries or consumes `maxContextRounds`).
2. When `!IsDrainPaused`, dequeue the next `ChatPendingInputItem` (`TryTakeNextQueued`) and run `ExecuteQueuedUserTurnAsync` appended to the active branch tail while preserving `PrefixState`.

### Mid-Turn Safe Steer / Wake Boundaries (`GenerateAsync` & `InvokeFunctionsAsync`)
1. **Protocol Pairing Invariant**: Every `FunctionCallContent` / `tool_use` must be immediately paired with its `ToolResultRecord` / `tool_result` before any subsequent `User` or `<background-job>` message.
2. **Pre-tool / Inter-tool Check**:
   - Before invoking tools in `GenerateAsync` or between serial slices in `InvokeFunctionsAsync`, if `PendingInputs.HasPendingSteers` is true, unstarted tools are skipped immediately (`AppendSkippedFunctionCallsAsync` / `MarkRemainingToolsSkippedAsync`) with synthetic skipped `ToolResultRecord` entries.
3. **In-flight Backgrounding (`SupportsBackgrounding`)**:
   - Tools running inside `InvokeFunctionsAsync` with `ChatFunction.SupportsBackgrounding == true` register into the active batch listener.
   - If a `Steer` arrives (or the user clicks `[Background]` on the live activity row), `FunctionCallContext.TryRequestMoveToBackground()` fires, resolving the foreground wait immediately with a synthetic `ToolResultRecord` referencing the assigned `jobId` (`bg-1`, ...), clearing the foreground card spinner (`IsBusy = false`), and continuing the turn immediately while keeping live `DisplaySink` output streaming to the card.
   - Non-backgroundable tools (`SupportsBackgrounding == false`) finish their short foreground step cleanly before the next join point injects the Steer.
4. **Join Point Injection (`InjectSteersAndWakesIfAnyAsync`)**:
   - After automatic compaction checks (if triggered) and before checking exit (`FunctionCalls.Count <= 0`), pending `Wake` notifications and `Steer` user messages are appended via `GenerationContext.Append(...)`, the current `AssistantChatMessage` is sealed, a fresh `AssistantChatMessage { IsBusy = true }` is opened, and `GenerateAsync` continues.

---

## 3. Opt-In Background Jobs (`SupportsBackgrounding`, `ChatBackgroundJobManager`, `manage_background_jobs`)

1. **`ChatFunction.SupportsBackgrounding`**: Explicit opt-in flag (`false` by default). Set `true` only on `execute_in_terminal` (`TerminalPlugin`) and `run_subagent` (`EssentialPlugin`).
2. **`ChatBackgroundJobManager` (`RootChatContext.BackgroundJobs`)**:
   - Detached invocations are transferred into `RootChatContext.StartBackgroundOperation(...)` using a session-lifetime-linked `CancellationTokenSource` (unlinked from foreground turn cancellation so `Steer` and foreground turn exit never kill backgrounded tools, while session shutdown cancels and awaits them cleanly).
   - `ActiveJobs` exposes running jobs to the composer tray with per-job `[Stop]` controls.
   - Completion always enqueues a `ChatBackgroundJobNotification` onto `PendingInputs` and wakes the session if idle (`ExecuteWakeTurnAsync`).
3. **Hidden `manage_background_jobs` Tool (`EssentialPlugin`)**:
   - Registered with `isVisible: false`, `isDefaultEnabled: true`, `supportsParallelInvoke: true` (hidden from user tool configuration UI while present in the default tool list to preserve prompt prefix cache stability).
   - Supports `list`, `output`, and `stop` actions reading live progress from `ChatPluginTerminalDisplayBlock.Run` and `ChatPluginSubagentDisplayBlock.ChatData`. Disabled inside nested subagent contexts.
4. **Approval & Interaction Independence**:
   - `WaitForUserInputAsync` / `RequestConsentAsync` / `AskQuestionAsync` pass `FunctionCallContext.ExecutionCancellationToken` (job-scoped) to `RootChatContext.SharedUserInterfaceBroker`, never cancelling in-flight dialogs on Steer or background move.
   - `GenerationContext` exposes `Assistant` and `IsDisposed`, and `FunctionCallContext` mirrors `FunctionCallContext.Current` (`AsyncLocal`), allowing `ReviewToolApprovalAsync` (`ToolApprovalMode.Auto`) to create an approval `ModelSession` cleanly even after the originating foreground `GenerationContext` has been disposed.
