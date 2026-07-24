# Strategy Engine Spec: Overview

## 1. Purpose

Strategy Engine 是 Everywhere 的上下文动作推荐系统。它读取用户当前上下文，推荐一组可点击的 Strategy；用户一次发送只允许绑定并执行一个 Strategy。被执行的 Strategy 负责产出本次请求的 prompt、system prompt override、工具规则集，以及后续可扩展的模型、附件、权限、上下文裁剪和后处理配置。

这不是一个任意脚本系统，也不是一个替代聊天输入框的自动化系统。它的第一职责是把 Everywhere 已经能感知到的上下文转化为低摩擦、可解释、可配置的行动入口。

```text
Context Snapshot
  -> Strategy Providers
  -> Static Dependency Analysis
  -> Extra Context Collection
  -> Condition Evaluation
  -> Recommended Strategies
  -> User Selects One Strategy
  -> Preprocessors
  -> Prompt + Ruleset + Options
  -> Chat Pipeline
```

## 2. Final Product Semantics

1. Strategy Engine 推荐可点击动作，而不是在每次普通聊天请求中隐式选择策略。
2. 推荐列表可以同时显示多个 Strategy。
3. 一次用户发送只允许执行一个 Strategy。
4. Strategy 可由用户通过 Markdown 文件编辑，也可由未来的图形化 Strategy Editor 编辑。
5. Strategy 文件格式是 `SKILL.md` 的上层能力，但不会自动把所有 `SKILL.md` 注册为全局 Strategy。
6. 用户可以手动导入或引用 `SKILL.md`，并配置 icon、name、priority、conditions、tools 等额外字段。
7. 第一版不允许用户覆盖 builtin Strategy。

## 3. Design Principles

| Principle | Requirement |
| --- | --- |
| Context first | Strategy 是否显示由上下文决定，不要求用户手动描述当前窗口、文件或选区。 |
| User understandable | 外部 DSL 面向非计算机用户，命名避免过度专业。例如使用 `extra` 而不是 `signals`、`facts`。 |
| Safe by construction | 用户可编辑文件不能执行任意代码；只能引用预定义 preprocessors、extra providers 和 tool rules。 |
| Explainable | 每个 Strategy 的推荐原因、读取的上下文、需要的权限和启用/禁用的工具都应能在 UI 中解释。 |
| Cross-platform first | 暴露给策略作者的 schema 尽量跨平台；平台细节隐藏在 provider 内部。 |
| Markdown-native | 普通用户可以通过 UI 编辑，开发者也可以直接编辑 `.strategy.md`。 |
| Backward-compatible with skills | `SKILL.md` 可以作为 Strategy 的 `from` 来源，但 Strategy 仍然是独立的可配置实体。 |

## 4. Explicit Non-goals for v1

The first implementation must not attempt to support:

1. Arbitrary user scripts in Strategy files.
2. Multiple Strategy composition for one user send.
3. User override of builtin Strategy IDs.
4. Automatic registration of every discovered `SKILL.md`.
5. Full XPath implementation.
6. Full policy engine for model selection, attachment mutation, postprocessors, or auto-approval.
7. Marketplace/distribution format for shared Strategy packages.

The architecture should leave extension points for these items, but the v1 implementation must remain smaller and auditable.

## 5. Current Implementation Baseline

The existing codebase already contains a useful skeleton:

```text
src/Everywhere.Core/StrategyEngine
  Abstractions/Strategy.cs
  Abstractions/StrategyContext.cs
  Abstractions/IStrategyEngine.cs
  Abstractions/IStrategyProvider.cs
  Abstractions/IStrategyPreprocessor.cs
  Conditions/*
  BuiltIn/*
```

Existing integration points:

1. `AddStrategyEngine()` registers builtin providers.
2. `StrategyContext.FromAttachments(...)` derives context from chat attachments.
3. `ChatWindowViewModel` calls `IStrategyEngine.GetStrategies(...)` when attachments change.
4. `UserStrategyChatMessage` persists the selected `Strategy`.
5. `ChatService` already reads `Strategy.ToolPatternRulesets`.
6. `ChatHistoryBuilder` already renders `Strategy.Body`.

Known gaps to address:

1. No user file provider.
2. No `.strategy.md` parser.
3. No `from` inheritance/reference system.
4. No `ExtraContext` provider pipeline.
5. No three-valued condition result.
6. No real preprocessor execution pipeline.
7. No visual query DSL parser/evaluator.
8. Current condition model is C# object based, not serializable user DSL.
9. Current provider namespace prefixing prevents builtin override semantics from being explicitly modeled.

## 6. Strategy Definition in One Example

```yaml
---
schema: everywhere.strategy/v1
id: user.file-manager.summarize-selection
enabled: true

name: "Summarize selected files"
description: "Summarize files selected in the file manager"
icon: FileText
priority: 80

when:
  all:
    - extra.file_manager.selection.items:
        count:
          min: 1
    - extra.file_manager.selection.items:
        any:
          extension:
            in: [".pdf", ".docx", ".txt", ".md"]

tools:
  builtin.filesystem.read_file: true
  builtin.web.*: false

preprocessors:
  - file-manager-selection

options:
  matchingTimeout: 300ms
  conditionTimeout: 80ms
  regexTimeout: 50ms
  visualQueryTimeout: 120ms
  extraTimeout: 200ms
---

Please summarize the selected files:

{extra.file_manager.selection.items}
```

The Strategy author only sees `extra.file_manager.*`. The implementation may satisfy that path through `windows.explorer`, `macos.finder`, or a future Linux provider. Provider identity is an implementation detail and should only appear in diagnostics/logs.

## 7. Glossary

| Term | Meaning |
| --- | --- |
| Strategy | A user-visible recommended action and its execution configuration. |
| Strategy Definition | A versioned authoring model such as `StrategyDefinitionV1`, parsed from `.strategy.md` or another source. |
| Strategy | The runtime normalized model used by matching, UI, execution, and chat messages. |
| Strategy Candidate | A matched Strategy plus evaluation metadata used for UI explanation and diagnostics. |
| Provider | A source that contributes Strategy definitions. |
| Extra Context | Additional context collected on demand, exposed under `extra.*` in user DSL. |
| Extra Context Provider | A platform or feature-specific collector that fills a subtree of `extra.*`. |
| Condition DSL | YAML structure under `when` that controls recommendation visibility. |
| Visual Query DSL | XPath-like subset used by `visual.exists`, `visual.count`, and `visual.match`. |
| Preprocessor | A predefined execution-time component that returns variables for prompt interpolation. |
| ToolPatternRulesets | Nested plugin/function glob rules controlling available chat tools. |
| `from` | A single source reference used to derive a Strategy from a skill, strategy, markdown file, absolute path, or URL. |

## 8. Document Map

| Document | Purpose |
| --- | --- |
| `01-Overview.md` | Product and architecture overview. |
| `02-CoreConcepts.md` | Canonical runtime model and data contracts. |
| `03-MatchingSystem.md` | Matching pipeline, three-valued logic, extra context, visual query evaluation. |
| `04-Preprocessors.md` | Execution pipeline and preprocessor behavior. |
| `05-ConfigurationFormat.md` | `.strategy.md` file format and DSL reference. |
| `06-ImplementationPlan.md` | Direct implementation steps, testing plan, and acceptance criteria. |
| `00-Skills.md` | Lightweight Skills registry, management UI, prompt injection, and complete `skill://{source}.{skill}` resolution. |
