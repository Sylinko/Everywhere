# Declarative Visual Context Testing Specification

## 1. Purpose

Visual Context reads live accessibility trees that may be large, fragmented, virtualized, mutable, slow, or broken. A few hand-written `IVisualElement` mocks are not sufficient to validate the replacement architecture.

This specification defines a small declarative UI model for building repeatable test scenarios. A scenario describes a UI in the same style as a declarative application: it composes windows, containers, text, controls, collections, and a small number of test behaviors. Random content is part of that control tree.

The same scenario is consumed by four backends:

```text
Scenario + Seed
      |
      v
VisualScenarioGenerator
      |
      v
Declarative VisualControl tree
      |
      +--> Mock backend
      +--> Windows Forms TestApp
      +--> Avalonia TestApp
      `--> CefSharp TestApp
```

The model is test infrastructure, not a production UI framework and not a general-purpose scenario language.

### 1.1 Staged Project Layout

The initial implementation is split by ownership:

| Project or location | Responsibility |
| --- | --- |
| `tests/Everywhere.VisualContext.Testing` | Declarative controls, path-addressed generation, Bogus-backed text, and the common/extreme scenario catalogs |
| `tests/Everywhere.Core.Tests/Chat/VisualContext/Testing` | Mock read session, operation counters, mutation, failure injection, and the temporary legacy `IVisualElement` adapter |
| `tests/Everywhere.VisualContext.TestApp.Shared` | JSON-lines revision protocol and exact-process controller |
| `tests/Everywhere.VisualContext.WinForms.TestApp` | Native Windows Forms projection and virtual-mode grids |
| `tests/Everywhere.VisualContext.Avalonia.TestApp` | Cross-platform Avalonia projection and indexed list source |
| `tests/Everywhere.VisualContext.CefSharp.TestApp` | Local HTML/ARIA projection and C#-backed virtual DOM paging |

`CefSharp.WinForms.NETCore` is referenced only by its isolated Windows TestApp. It is not a transitive dependency of production Visual Context, the shared scenario model, or the other TestApps.

## 2. Design Principles

1. **Scenarios look like UI.** Authors compose familiar controls instead of manually wiring accessibility interfaces or serialized node graphs.
2. **Scenario and seed are the reproduction boundary.** Within the same commit and test configuration, the same scenario and seed produce the same logical control tree and behavior sequence.
3. **Hand-written structure, generated detail.** A scenario author chooses the meaningful application shape. Seeded controls generate names, text, counts, nesting, and other detail.
4. **Large indexed collections stay lazy.** A virtual list with one hundred thousand logical items must not require one hundred thousand objects in the test process. This rule does not require text already owned by the target application to be replaced with a synthetic lazy string source.
5. **Mutation follows logical operations.** Mutable scenarios advance on operations such as `MoveNext`, not on wall-clock delays.
6. **Backends are renderers.** They expose the same logical scenario through a mock provider or a real UI toolkit. They do not redefine scenario meaning.
7. **The tested algorithm remains independent.** Shared scenario code must not reuse Visual Context traversal, planning, compression, or rendering logic.
8. **Platform projections may differ.** Equivalent logical controls may produce different UIA, AX, or AT-SPI wrapper nodes. Real-process assertions therefore use semantic anchors and safety invariants rather than exact full-tree snapshots.
9. **Keep the model small.** Add a control or behavior only when a concrete scenario cannot be expressed clearly with existing composition.

## 3. Reproducibility Contract

The required contract is intentionally local to a source revision:

```text
same commit + same scenario + same seed + same test configuration
    => same logical control tree and logical behavior sequence
```

Cross-commit compatibility is not required. When generator code or a scenario changes, its output may change. CI already identifies the commit; a failing randomized test must additionally report the scenario name and seed.

The generator must not depend on process-randomized hashes, unordered collection enumeration, current culture, wall-clock time, or thread scheduling. Random values used by lazy controls must be addressable: asking for item 50,000 must produce the same item without first generating items 0 through 49,999.

An implementation may derive local random streams from stable inputs such as:

```text
scenario seed + logical control path + feature name + item index
```

The exact random algorithm is an implementation detail within one commit. It only needs to be shared by every process that regenerates the same scenario during that test run.

## 4. Declarative Scenario Model

### 4.1 Authoring Shape

The final C# API should remain close to this illustrative form:

```csharp
Scenario.Define("chat", context =>
    new Window(
        new VerticalStack(
            context.RandomText("title", ScenarioTextKind.Title),
            new VirtualList(
                context,
                "messages",
                context.RandomInt("message-count", 20, 200),
                (itemContext, index) => new HorizontalStack(
                    itemContext.RandomText("user", ScenarioTextKind.UserName),
                    itemContext.RandomText("message", ScenarioTextKind.Message),
                    new Button("Reply"))),
            new TextBox(context.RandomTextValue("draft", ScenarioTextKind.Sentence)))));
```

The names and constructors are not frozen by this document. The required property is that a scenario reads as a declarative UI definition rather than an accessibility-provider implementation.

`VisualScenarioGenerator` supplies the seeded context and returns the root control or roots. It is not a compiler pipeline and does not create production `CapturedVisualContext`, `RenderPlan`, or `VisualTarget` objects.

### 4.2 Common Control Set

The first implementation should provide only the controls needed by the initial scenarios.

| Category | Controls |
| --- | --- |
| Roots and structure | `Window`, `Panel`, `VerticalStack`, `HorizontalStack`, `Group` |
| Passive content | `Text`, `FragmentedText`, `Document`, `Image` |
| Interaction | `Button`, `Link`, `TextBox`, `CheckBox`, `RadioButton`, `ComboBox`, `Slider`, `ProgressBar` |
| Collections | `ListBox`, `VirtualList`, `Tree`, `Table`, `Repeat` |
| Application structure | `TabControl`, `TabItem`, `MenuBar`, `MenuItem`, `Separator`, `Dialog`, `ToolBar`, `StatusBar` |
| Mutation | `OnMoveNext` |

This is a starting set, not a requirement to implement every control before the first test. Backends may initially support a smaller declared subset and must report unsupported controls explicitly.

Common scenarios are application prototypes rather than minimal control demonstrations. Their hand-written skeletons should contain the recognizable shell of the application category: primary navigation, commands or tools, the main work area, relevant auxiliary panes, and status or completion actions. UI behavior may be omitted when it is irrelevant to visual-context capture, but removing the surrounding application structure is not an acceptable simplification.

Structural controls retain their platform semantics. For example, `MenuBar` and nested `MenuItem` declarations project to native menu controls or equivalent HTML menu roles, while `TabControl` contains named `TabItem` pages. A renderer must not flatten these declarations into generic unnamed stacks merely because that is easier to display.

### 4.3 Common Properties

Controls may expose the smallest useful semantic set:

- a stable logical path generated from the declaration and item index;
- type or role;
- name and text values;
- states such as focused, selected, disabled, read-only, password, and off-screen;
- action capabilities;
- semantic test anchors, including core-element anchors;
- children or an indexed lazy child factory;
- optional layout hints when a scenario depends on relative geometry.

The logical path is test identity. It is not an expected platform runtime ID and is never presented to the Agent as a real target ID.

### 4.4 Random Content as UI

Random content belongs in the declaration:

```csharp
context.RandomText("body", ScenarioTextKind.Paragraph)
new Repeat(context, "items", context.RandomInt("count", 50, 501), (itemContext, index) => ...)
context.RandomBool("use-button") ? new Button("Continue") : new Link("Continue")
new FragmentedText(
    context.RandomTextValue("paragraph", ScenarioTextKind.Paragraph),
    context.RandomInt("fragments", 10, 101))
```

Bogus is the primary source for names, sentences, paragraphs, and locale-specific fake data. Hand-written text is reserved for concrete gaps that the package cannot express. The resulting text set should cover common and difficult content without using private captured user data:

- Chinese, English, and mixed-language prose;
- emoji, combining characters, and right-to-left text;
- code, logs, tables, and long paragraphs;
- empty strings, repeated names, and equivalent name/text pairs;
- JSON, XML, and Markdown punctuation;
- one very long line and many very short fragments.

An extreme text scenario deliberately models a target that already owns a very large string. `ExtremeScenarios.SingleLongText`, for example, materializes approximately one million characters in the target process. That is not a test-infrastructure failure and must not be replaced with a platform-independent lazy string merely to satisfy the observing client's budget.

The boundedness requirement begins at the accessibility API call made by the client. A successful bounded read must prevent the complete value from crossing the process boundary; receiving a complete value and slicing it locally is not bounded provider access. The concrete capability depends on the target provider and platform:

| Platform capability | Safe bounded use | Unbounded or separate concern |
| --- | --- | --- |
| Windows UIA TextPattern | Obtain the document or another text range and call `IUIAutomationTextRange.GetText(maxLength)`. The provider returns at most the requested number of characters. | `IUIAutomationValuePattern.CurrentValue`, legacy accessible values, and ordinary string properties have no maximum-length parameter. UIA cache requests select fields and patterns but do not cap string payload size. |
| Windows UIA timeouts | `IUIAutomation2.ConnectionTimeout` bounds the wait while obtaining an element; `TransactionTimeout` bounds a provider information request. | Timeouts limit waiting, not returned text length, traversal count, or memory usage. |
| macOS AX ranged text | Read `AXNumberOfCharacters` when needed and call `AXUIElementCopyParameterizedAttributeValue` with `AXStringForRange` and an `AXValue`-wrapped `CFRange`. | Reading the complete `AXValue` and then slicing it is not bounded. `AXUIElementSetMessagingTimeout` limits messaging time, not payload size. |
| macOS AX array attributes | Use `AXUIElementCopyAttributeValues(index, maxValues)` for large array-valued attributes such as children. | Array paging is not a substitute for ranged text and applies only when the attribute value is an array. |

Providers do not all expose ranged text. The platform adapter must discover the supported capability, prefer the provider-bounded operation, and otherwise report text as unavailable or explicitly unbounded according to the read policy. It must not silently fall back to a whole-value read when a bounded request was required.

### 4.5 Lazy Collections

`VirtualList`, `Repeat`, `Tree`, and `Table` may describe an indexed item factory rather than store all children:

```csharp
new VirtualList(
    count: 100_000,
    item: index => new Text($"Item {index}"));
```

Creating the scenario, starting a backend, and reading the first bounded page must remain proportional to the observed page, not the logical collection size.

## 5. Logical Mutation

### 5.1 MoveNext Steps

Mutable scenarios use one monotonic logical step. Every intercepted `MoveNext` attempt advances the scenario exactly once. The mutation is applied before the underlying enumeration reads its next item:

```text
MoveNext attempt
    -> advance scenario step
    -> apply deterministic mutation for that step
    -> expose updated state
    -> perform underlying MoveNext
```

The initial state is step 0. The first `MoveNext` observes step 1. An attempt consumes its step even when the underlying provider later returns `false`, times out, or fails.

An illustrative declaration is:

```csharp
new OnMoveNext(step =>
    step % 2 == 0
        ? new Text("Even state")
        : new Text("Odd state"));
```

The generator may also use the scenario seed and step to insert, remove, reorder, or replace children deterministically.

### 5.2 Backend Coordination

The Mock backend advances the step directly.

Real-process tests use a test-only session decorator and a small TestApp control channel:

```text
test Enumerator.MoveNext
    -> TestApp AdvanceScenario
    -> TestApp applies the new revision
    -> TestApp acknowledges the revision
    -> real platform Enumerator.MoveNext
```

This coordination must be bounded. It must not add production-only test hooks to Visual Context and must not use timers to guess when a UI change has completed.

## 6. Backends

### 6.1 Mock Backend

The Mock backend is implemented first. It directly projects declarative controls through a test-only read-session model that describes the required observable behavior without adding or freezing a production platform interface. During migration it may also expose a legacy `IVisualElement` adapter so the current and replacement traversal algorithms can consume the same stable scenario.

The Mock backend must support:

- lazy parent, child, and sibling enumeration;
- known and unknown counts;
- deterministic `HasMore` and lookahead behavior;
- operation counters;
- bounded text reads;
- unavailable and unsupported fields;
- timeout and provider-failure injection;
- `MoveNext`-driven mutation;
- disposal tracking for Enumerators and sessions.

It must not call the real Capturer, Planner, Renderer, or target registry to calculate expected results.

### 6.2 Windows Forms TestApp

The Windows Forms backend renders scenarios as real Windows controls in a separate process. It is the primary Windows control/provider fixture and should include virtualized or virtual-mode collections where the framework supports them.

Windows Forms is not treated as a raw Win32 UIA provider. If future failures require direct HWND or custom-provider behavior that Windows Forms cannot express cleanly, a smaller dedicated Win32 provider fixture may be added later.

### 6.3 Avalonia TestApp

The Avalonia backend renders the same declarative scenarios through the UI framework used by Everywhere. It provides cross-platform coverage through the Windows, macOS, and Linux accessibility bridges.

The backend may add toolkit-required wrapper controls, but semantic anchors and logical order must remain discoverable. Tests must not assume that the resulting platform tree is structurally identical across operating systems.

### 6.4 CefSharp TestApp

The CefSharp backend simulates Chromium-based and Electron-like application surfaces on Windows.

It loads a fixed local HTML skeleton and a prewritten JavaScript runtime. C# calls a small stable JavaScript API instead of generating ad hoc DOM-manipulation scripts for each test:

```javascript
globalThis.everywhere = {
    render(definition, step) {},
    updateVirtualPage(path, start, children, childCount) {}
};
```

The runtime maps declarative controls to HTML and ARIA and applies deterministic mutation steps. A virtual list sends a bounded `virtualPage` request through `CefSharp.PostMessage`; C# generates only that indexed page from the declarative scenario and calls `updateVirtualPage` to replace the realized DOM window. The TestApp reports ready only after the skeleton, JavaScript context, initial scenario, and accessibility prerequisites are established.

All content is local and deterministic. The backend must not require internet access. CefSharp and its native assets remain test-only dependencies and do not flow into production projects.

## 7. TestApp Process Contract

Each real backend is a separate executable with its own normal UI bootstrap. A shared controller starts the selected target process with at least:

```text
--scenario <name>
--seed <number>
```

The target reports:

- process identity;
- native root window identity where available;
- readiness;
- current scenario step and revision;
- semantic anchor root indexes, logical paths, keys, and platform-facing automation IDs;
- unsupported scenario features;
- fatal target-side errors.

The controller owns only the process it launched and must terminate that exact process during cleanup. It resolves core elements through production platform entry points and reads them through `VisualContextService.EnterScopeAsync` once that API exists.

## 8. Scenario Catalog

### 8.1 Common Application Shapes

The first catalog should include hand-written structural templates for:

- chat and message history;
- social or news feed;
- browser article and form;
- IDE project tree, editor, and error list;
- file explorer;
- settings form;
- table or spreadsheet-like grid;
- document editor;
- terminal or log viewer;
- menus, dialogs, tabs, and multiple roots.

Each template uses seeded controls for text, counts, optional branches, and small structural variation.

### 8.2 Required Extreme Shapes

- one element with extremely long text;
- one parent with a very large lazy child count;
- a paragraph split into many passive text elements;
- deep runs of semantically empty containers;
- mixed passive content and interactive descendants;
- several core elements under one native root;
- genuinely disconnected roots;
- mutation on every `MoveNext`;
- unavailable elements and provider failures;
- repeated equal-priority structures.

## 9. Assertions

Mock tests may assert exact logical traversal and operation counts. Real-process tests primarily assert semantic anchors and invariants:

- the operation terminates inside configured bounds;
- platform calls, enumerated children, captured nodes, and captured text remain bounded;
- the expected core anchors remain represented or explicitly omitted;
- no target ID silently changes meaning;
- Element and Composite capabilities remain honest;
- partial results retain correct omission reasons;
- unrelated branches remain eligible after a branch failure;
- all Enumerators, sessions, and scopes are released;
- Plan and Render do not perform platform reads;
- equivalent stable observations produce deterministic logical output.

Full UIA, AX, or AT-SPI tree snapshots are diagnostic artifacts, not the primary cross-version correctness oracle.

## 10. Execution Tiers

| Tier | Contents | Default execution |
| --- | --- | --- |
| Mock deterministic | Hand-written scenarios and fixed seeds | Every relevant test run |
| Mock generated | A bounded set of reported seeds | Pull requests and larger CI runs |
| Controlled TestApps | WinForms, Avalonia, and CefSharp target processes | Platform integration or nightly runs |
| Input-guard integration | Real low-level hooks and injected-input tags | Isolated desktop or VM only |
| Real third-party applications | Diagnostic profiles for installed software | Explicit/manual only |

A randomized failure must print its scenario and seed. CI artifacts may additionally retain bounded traces and rendered results. They must not capture unrelated desktop content.

## 11. Implementation Plan

### Phase 1: Declarative Generator

1. Add the shared test-only control model.
2. Implement `VisualScenarioGenerator` and seeded random helpers.
3. Add random text sources and lazy collection controls.
4. Prove that repeated generation with one scenario and seed is identical within the same build.

### Phase 2: Mock Backend

1. Project the declarative tree through the test-only bounded read-session model.
2. Add operation counters, failures, Enumerator metadata, and disposal tracking.
3. Add the legacy `IVisualElement` adapter needed to characterize the current builder.
4. Implement a small set of end-to-end scenarios before expanding the catalog.

### Phase 3: Scenario Coverage

1. Add the common application templates.
2. Add extreme text, collection, fragmentation, and container cases.
3. Add `MoveNext`-driven mutable scenarios.
4. Add bounded generated-seed runs and failure reproduction output.

### Phase 4: Real TestApps

1. Add the shared process controller and revision protocol.
2. Implement the Windows Forms renderer.
3. Implement the Avalonia renderer.
4. Implement the CefSharp skeleton, JavaScript runtime, and renderer.
5. Run the same scenario names and seeds through Mock and supported real backends.

A minimal smoke scenario may be brought up on each real backend earlier if needed to keep the declarative model honest. Bulk backend coverage remains Phase 4.

The controlled process smoke tier is explicit because it opens real windows. It can be run on an interactive Windows desktop after building the three TestApps:

```powershell
dotnet test tests/Everywhere.Core.Tests/Everywhere.Core.Tests.csproj `
  --filter TestCategory=ControlledTestApp -- NUnit.ExplicitMode=Relaxed
```

Temporary gaps that must be closed by later stages are tracked in the repository-root `temp.md` rather than hidden behind test-only fallbacks.

## 12. Non-Goals

- A general UI framework or production abstraction.
- A user-authored JSON, XML, or YAML scenario language.
- Stable generated output across unrelated commits.
- Identical accessibility-tree shapes across platforms or toolkit versions.
- Materializing the complete logical tree before a test starts.
- Testing arbitrary third-party applications in required CI.
- Replacing focused unit tests for isolated Planner, Renderer, registry, and scope behavior.

## 13. Initial Acceptance Criteria

The first useful milestone is complete when:

1. one declarative scenario containing ordinary controls, random text, and a lazy collection can be generated repeatedly from a reported seed;
2. the Mock backend exposes it through bounded parent, child, and sibling enumeration;
3. the current Weighted BFS can consume it through the legacy adapter;
4. a mutable scenario changes deterministically once per `MoveNext` attempt;
5. a large logical child count does not cause proportional setup allocation;
6. a failure reports enough information to rerun the same scenario and seed from the same commit.
