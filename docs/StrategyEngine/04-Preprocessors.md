# Strategy Engine Spec: Preprocessors and Execution Pipeline

## 1. Purpose

Preprocessors run after the user chooses a Strategy and before the prompt is rendered for the chat model.

They are not part of condition matching. Matching decides whether a Strategy is recommended. Preprocessors prepare execution-time variables for the one Strategy the user actually executes.

v1 preprocessors must be predefined by Everywhere or trusted plugins. Strategy files can reference them by ID, but cannot define arbitrary executable code.

## 2. v1 Scope

v1 preprocessors can:

1. Read the current `StrategyContext`.
2. Read `extra` values already collected for matching when available.
3. Perform additional execution-time extraction.
4. Return template variables using path-style keys.
5. Return diagnostics.
6. Fail and stop Strategy execution.

v1 preprocessors cannot:

1. Execute arbitrary user-authored scripts.
2. Modify `ToolPatternRulesets`.
3. Modify model selection.
4. Add or remove chat attachments.
5. Perform postprocessing on assistant output.
6. Automatically grant tool permissions.

The result shape should leave room for future patches, but v1 should only implement variables.

## 3. Execution Lifecycle

```text
1. User sees recommended Strategies
2. User selects one Strategy
3. User optionally enters additional request text
4. User sends
5. Engine creates execution context
6. Engine runs preprocessors in declared order
7. Engine merges variables
8. Engine renders Strategy body/system prompt
9. Engine applies Strategy ToolPatternRulesets
10. Engine creates UserStrategyChatMessage
11. ChatService generates response
```

This keeps the existing Everywhere interaction model: the Strategy can be selected, then the user can provide an optional argument. A future one-click Strategy may send immediately when it has no required user argument, but v1 should not require that UI behavior.

## 4. Relationship to Existing Code

Existing objects:

1. `UserStrategyChatMessage` already stores `Strategy` and optional `PreprocessorResult`.
2. `ChatService` already reads `Strategy.ToolPatternRulesets`.
3. `ChatHistoryBuilder` already calls `RenderStrategyUserPrompt`.
4. `ScopedPromptRenderer` already supports `{Argument}` and preprocessor variables.

Required changes:

1. Execute preprocessors before constructing/sending `UserStrategyChatMessage`, or before rendering history if delayed execution is chosen.
2. Persist `PreprocessorResult` in `UserStrategyChatMessage` so retries/replays are stable.
3. Update interpolation to support path-style variables.
4. Preserve existing `{Argument}` behavior as a compatibility alias.

## 5. Preprocessor Registry

Recommended interface:

```csharp
public interface IStrategyPreprocessor
{
    string Id { get; }
    IDynamicLocaleKey DisplayNameKey { get; }
    IDynamicLocaleKey DescriptionKey { get; }
    IDynamicLocaleKey PermissionDescriptionKey { get; }

    Task<PreprocessorResult> ProcessAsync(
        StrategyExecutionContext context,
        CancellationToken cancellationToken = default);
}
```

The registry should support:

```csharp
public interface IStrategyPreprocessorRegistry
{
    bool TryGet(string id, out IStrategyPreprocessor preprocessor);
    IReadOnlyList<IStrategyPreprocessor> GetAll();
}
```

Unknown preprocessor IDs are validation errors when possible. If a preprocessor disappears after validation, execution must fail with a user-readable error and must not send the prompt.

## 6. StrategyExecutionContext

Execution context should contain the normalized Strategy, the current input context, and the user's optional argument.

```csharp
public sealed record StrategyExecutionContext
{
    public required Strategy Strategy { get; init; }
    public required StrategyContext StrategyContext { get; init; }
    public string? UserInput { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
```

If matching produced a `StrategyCandidate`, execution may reuse candidate diagnostics and collected `extra`. If context is stale, implementation may refresh required extra before preprocessing.

## 7. PreprocessorResult

Recommended v1 result:

```csharp
public sealed record PreprocessorResult
{
    public IReadOnlyDictionary<string, object?> Variables { get; init; } =
        new Dictionary<string, object?>();

    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}
```

Variable keys must use path style:

```text
preprocess.browser.url
preprocess.browser.readable_text
preprocess.file_manager.selection.text
preprocess.file_manager.selection.paths
```

Do not use PascalCase variables for new preprocessors. Existing variables may be supported as aliases during migration.

## 8. Variable Merge Rules

Sources available to prompt rendering:

1. Built-in prompt variables already supported by Everywhere.
2. `attachments.*`
3. `clipboard.*`
4. `assistant.*`
5. `environment.*`
6. `extra.*`
7. `preprocess.*`
8. Compatibility alias `{Argument}`

Recommended precedence:

```text
preprocess.* exact key
extra.* exact key
attachments/clipboard/assistant/environment exact key
built-in prompt variables
compatibility aliases
```

Preprocessors should not write into `extra.*` or `attachments.*`; they should write into `preprocess.*`. The renderer can still expose context values directly to templates.

If duplicate keys are produced by multiple preprocessors:

1. Later preprocessors override earlier preprocessors.
2. A diagnostic should be recorded for duplicate keys.
3. Override should be deterministic by declared preprocessor order.

## 9. Template Rendering

Strategy body is Markdown with variable placeholders.

```markdown
Please summarize:

{extra.file_manager.selection.items}

Additional request:
{Argument}
```

Rules:

1. Placeholder syntax is `{path.to.value}`.
2. Missing value should render as an empty string by default or remain unresolved only in diagnostics mode. Prefer empty string for user-facing prompt cleanliness.
3. Missing required preprocessor output should be detected by preprocessor failure, not by renderer guessing.
4. Non-string values should be rendered with a stable readable serialization.
5. Arrays should render as readable lists.
6. Objects should render as compact YAML or JSON-like blocks.
7. Sensitive values should not be logged in diagnostics.

`{Argument}` compatibility:

1. `{Argument}` resolves to the user input text.
2. New Strategy files should prefer `{input}` or `{user.input}` only if those aliases are formally added later.
3. v1 keeps `{Argument}` because the current renderer supports it.

## 10. Body and User Input Semantics

Current behavior appends user input under `<UserRequestStart>` when body and user input both exist. v1 may keep this for compatibility, but new rendering semantics should be explicit:

1. If Strategy body exists, render it as the main user message.
2. If user input exists and the body does not contain `{Argument}`, append user input in a clearly delimited section.
3. If body contains `{Argument}`, do not append the user input a second time.
4. If body does not exist, send the user input as normal chat content.

Recommended appended section:

```text
<UserRequestStart>
...
```

Do not change this marker in v1 unless the chat history builder is updated consistently.

## 11. System Prompt

Strategy may define `systemPrompt`.

```yaml
systemPrompt: |
  You are an expert legal summarizer.
```

Rules:

1. If `systemPrompt` is null, use the assistant default.
2. If `systemPrompt` is present, pass it as system prompt override for this request.
3. It supports the same variable renderer as Strategy body.
4. Preprocessor variables must be available before rendering system prompt.
5. Rendering errors stop execution.

## 12. ToolPatternRulesets Application

Strategy tool rules are applied for the request executing that Strategy.

Current layering in `ChatService` is close to:

```text
default persistent tool settings
-> Strategy.ToolPatternRulesets
-> ChatContext.ToolPatternRulesets
```

v1 should preserve this behavior unless a separate plugin policy spec changes it.

Rules:

1. Strategy tool rules must not permanently change global user settings.
2. Strategy tool rules must be persisted in the user message so retry/replay remains consistent.
3. If a Strategy enables a tool that still requires runtime consent, existing consent UI remains authoritative.

## 13. Permissions and Disclosure

Preprocessors must expose permission description keys:

```csharp
IDynamicLocaleKey PermissionDescriptionKey { get; }
```

Strategy details UI should aggregate permissions from:

1. `when` context paths.
2. `extra` providers inferred by matching.
3. Preprocessors.
4. ToolPatternRulesets.

Examples:

```text
Reads selected text
Reads clipboard text
Reads visible UI information
Reads file manager current folder and selection
Allows file read tool
Allows web search tool
```

v1 does not require a new consent prompt for every preprocessor, but the metadata must exist.

## 14. Error Handling

If any preprocessor fails:

1. Stop execution.
2. Do not send prompt to the LLM.
3. Show a user-readable error or toast.
4. Store diagnostics if a failed execution record is created.

Failure categories:

```text
preprocessor.not_found
preprocessor.timeout
preprocessor.permission_unavailable
preprocessor.context_missing
preprocessor.exception
preprocessor.invalid_result
```

Timeout is failure during execution, not a silent non-match. Matching timeouts hide a Strategy; execution timeouts stop a user-requested action and explain why.

## 15. Cancellation

Preprocessors must honor cancellation tokens.

Cancellation sources:

1. User cancels the send.
2. Chat context is cancelled.
3. Preprocessor timeout.
4. Application shutdown.

Cancelled execution must not create a partially rendered prompt.

## 16. Suggested Built-in Preprocessors for v1

The exact list can be smaller in the first PR, but the design should support:

| ID | Purpose |
| --- | --- |
| `selected-text` | Expose selected text as `preprocess.selection.text`. |
| `file-manager-selection` | Expand file manager selection into paths/content summaries. |
| `browser-active-page` | Expose URL/title and possibly readable text. |
| `clipboard-text` | Expose clipboard text. |
| `visual-element-text` | Extract text from the primary visual element. |

Each built-in preprocessor must have:

1. Stable ID.
2. Display name.
3. Description.
4. Permission description.
5. Unit tests.
6. Timeout behavior.

## 17. Execution Diagnostics

Execution diagnostics should be visible in Strategy diagnostics and logs.

Recommended fields:

```text
strategy id
preprocessor id
duration
variables produced, names only
failure category
exception type, if any
```

Do not log full variable values by default.

## 18. Future Extensions

The result type can later grow optional patches:

```csharp
public sealed record PreprocessorResult
{
    public IReadOnlyDictionary<string, object?> Variables { get; init; }
    public ToolPatternRulesets? ToolPatternRulesetsPatch { get; init; }
    public IReadOnlyList<ChatAttachment>? AdditionalAttachments { get; init; }
    public StrategyModelOptions? ModelOptionsPatch { get; init; }
}
```

These fields are intentionally out of v1 scope. Do not implement them until the matching and file format are stable.
