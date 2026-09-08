# Declarative Visual Context Testing Specification

## 1. Purpose

Visual Context reads live accessibility trees that may be large, fragmented, virtualized, mutable, slow, or broken. A few hand-written element mocks are not sufficient to validate the replacement architecture.

This specification defines a small declarative UI model for building repeatable test scenarios. A scenario describes a UI in the same style as a declarative application: it composes windows, containers, text, controls, collections, and a small number of test behaviors. Random content is part of that control tree.

The target architecture and acceptance checklist are summarized in [Visual Context Architecture](02-Architecture.md) and [Visual Context Verification](08-Verification.md). This document remains the detailed source of truth for the test infrastructure itself.

The same deterministic scenario is consumed by four backends:

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

Real-web exploration is a separate, deliberately non-deterministic path. An Avalonia `NativeWebView` TestApp hosts the platform browser engine and is observed through the production platform Automation Backend. It does not pretend that an arbitrary website belongs to the `scenario + seed` contract.

### 1.1 Staged Project Layout

The initial implementation is split by ownership:

| Project or location | Responsibility |
| --- | --- |
| `src/Everywhere.Prompting` | Shared `PromptNode` model, native `PromptElement` structure, prompt pruning, and approximate token estimation used by Visual Context output |
| `src/Everywhere.Automation` | Context-owned `VisualElement`, sealed platform-neutral `VisualContext`, `IVisualElementBackend`, `VisualElementRetention`, bounded query results, failure semantics, and Enumerator contracts consumed by production platforms and the Mock |
| `tests/Everywhere.Automation.Testing` | Declarative controls, path-addressed generation, Bogus-backed text, and the common/extreme scenario catalogs |
| `tests/Everywhere.Automation.Tests/Testing` | Production-contract Mock Backend, operation counters, mutation, failure injection, and retention/lifetime validation |
| `tests/Everywhere.Automation.TestApp.Shared` | JSON-lines revision protocol and exact-process controller |
| `tests/Everywhere.Automation.WinForms.TestApp` | Native Windows Forms projection and virtual-mode grids |
| `tests/Everywhere.Automation.Avalonia.TestApp` | Cross-platform Avalonia projection and indexed list source |
| `tests/Everywhere.Automation.CefSharp.TestApp` | Local HTML/ARIA projection and C#-backed virtual DOM paging |
| `tests/Everywhere.Automation.WebView.TestApp` | Cross-platform native-browser host for non-deterministic real-web observations |
| `tests/Everywhere.Automation.WebView.Probe` | Batch and Streamable HTTP MCP host for interactive real-web retrieval journeys; the first platform composition uses the Windows Backend |
| `tests/Everywhere.Automation.Windows.Tests` | Windows-only controlled-process and production UIA integration tests, with direct production references and MSBuild-generated TestApp launch paths |

`CefSharp.WinForms.NETCore` is referenced only by its isolated Windows TestApp. It is not a transitive dependency of production Visual Context, the shared scenario model, or the other TestApps.

`Avalonia.Controls.WebView` is likewise isolated to the real-web TestApp. The Probe controls that process and observes it through a production platform Backend; it does not reference or automate the browser engine in-process.

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
10. **Faults belong to provider behavior, not fake UI structure.** Mock failure schedules and TestApp responsiveness controls are test-harness behavior addressed to real logical paths or roots. They do not introduce fictional timeout controls into the declarative application.

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

`VisualScenarioGenerator` supplies the seeded context and returns the root control or roots. It is not a compiler pipeline and does not create production `VisualContextSnapshot`, `PromptNode`, or `VisualTarget` objects.

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

Common scenarios are application prototypes rather than minimal control demonstrations. Their hand-written skeletons should contain the recognizable shell of the application category: primary navigation, commands or tools, the main work area, relevant auxiliary panes, and status or completion actions. UI behavior may be omitted when it is irrelevant to visual-context snapshots, but removing the surrounding application structure is not an acceptable simplification.

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

Text observation has two independent boundaries. Provider liveness is protected by the native UIA or AX timeout, while Snapshot Traverser bounds aggregate elapsed time and operation count between calls. `MaxTextCharacters` bounds the preview returned to Snapshot and the Agent, but it does not require every provider to support ranged transfer. The concrete capability depends on the target provider and platform:

| Platform capability | Preferred or common use | Boundary |
| --- | --- | --- |
| Windows UIA ValuePattern | Read cached `CurrentValue` and truncate the managed preview locally. This is the common scalar text capability. | `TransactionTimeout` protects the provider call; the RPC payload may be larger than the returned preview. Traverser separately bounds the call series. |
| Windows UIA TextPattern | For documents and other providers that expose ranged content, obtain a range and call `IUIAutomationTextRange.GetText(maxLength)`. | TextPattern is less commonly available than ValuePattern and supplements rather than defines safe text access. |
| macOS AX text | Prefer `AXStringForRange` when supported; otherwise read the ordinary Value attribute and truncate the returned preview locally. | `AXUIElementSetMessagingTimeout` protects one native message; Traverser separately bounds the call series. |
| macOS AX array attributes | Use `AXUIElementCopyAttributeValues(index, maxValues)` for large array-valued attributes such as children. | Array paging is not a substitute for ranged text and applies only when the attribute value is an array. |

Providers do not all expose ranged text. The platform adapter uses the ordinary Value capability for scalar text and may use ranged access when the element exposes document-style content. `VisualElementSnapshot.HasMoreText` records content beyond the bounded preview and becomes `moreText`, without failure status. Snapshot makes no global completeness claim; tests assert observed content, concrete limitations, and resource release. Tests distinguish provider-side ranging from local preview truncation for performance characterization, but both are valid query paths under the same native timeout and Snapshot aggregate bounds.

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

Real-process tests use a test-only Enumerator/operation coordinator and a small TestApp control channel:

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

The current Mock fixture uses Context-owned `Everywhere.Automation.VisualElement` subclasses with the same bounded element and Enumerator contracts as production platforms. It deliberately remains a fixture-scoped Context/Backend aggregate for simple scenario tests rather than pretending to be the process-singleton production `IVisualElementBackend`; Backend root-acquisition conformance can be split out when those tests are designed. Its former temporary `Everywhere.Interop.IVisualElement` surface and the legacy Builder tests have been removed; Snapshotter and prompt-projection tests consume the canonical element model directly.

The Mock backend must support:

- lazy parent, child, and sibling enumeration;
- known and unknown counts;
- deterministic `HasMore` and lookahead behavior;
- operation counters;
- bounded text reads;
- unavailable and unsupported fields;
- deterministic scalar-query, Enumerator, timeout, and provider-failure injection addressed by logical path and operation;
- repeated same-provider failures and exact operation counters for circuit-breaker and no-retry assertions;
- `MoveNext`-driven mutation;
- disposal tracking for Enumerators, retention batches, Contexts, and Runtimes.

Mock failure injection does not sleep to imitate a timeout. It returns or throws the configured semantic failure at the configured operation, allowing tests to assert exact attempts, partial results, status propagation, and later-query recovery deterministically.

It must not call the real Capturer, merged PromptNode builder, or `VisualContext` publication logic to calculate expected results.

The shared Backend fixture verifies root acquisition into independent caller Contexts, fixed native timeout policy, and shared-resource disposal without retaining Contexts or Elements. Mock provider failures deliberately do not claim thread or process containment. Real UIA or AX processes remain required to characterize native timeout behavior, while only a future isolated query-host process can prove kill-and-restart containment for a call that ignores the platform boundary.

### 6.2 Windows Forms TestApp

The Windows Forms backend renders scenarios as real Windows controls in a separate process. It is the primary Windows control/provider fixture and should include virtualized or virtual-mode collections where the framework supports them.

Windows Forms is not treated as a raw Win32 UIA provider. If future failures require direct HWND or custom-provider behavior that Windows Forms cannot express cleanly, a smaller dedicated Win32 provider fixture may be added later.

### 6.3 Avalonia TestApp

The Avalonia backend renders the same declarative scenarios through the UI framework used by Everywhere. It provides cross-platform coverage through the Windows, macOS, and Linux accessibility bridges.

The backend may add toolkit-required wrapper controls, but semantic anchors and logical order must remain discoverable. Tests must not assume that the resulting platform tree is structurally identical across operating systems.

Real backends expose only responsiveness behavior they can implement through their actual toolkit and accessibility provider. A blocked UI dispatcher is a truthful whole-root or whole-process unresponsive fixture when provider work is dispatched there. Element-scoped or operation-scoped native failures require a real custom provider boundary; they must not be claimed merely because the Mock can inject them.

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

Scenario content is local and deterministic. CefSharp accepts only `scenario + seed`, does not navigate arbitrary external pages, and must not require internet access for required tests. CefSharp and its native assets remain test-only dependencies and do not flow into production projects.

### 6.5 Native WebView TestApp

The real-web TestApp uses Avalonia `NativeWebView` rather than CefSharp. Avalonia provides the common window and control-channel lifecycle while the embedded provider remains the platform browser: WebView2 on Windows, WKWebView on macOS, and a supported WebKit implementation on Linux. This intentionally tests different real accessibility providers instead of promising identical browser trees across platforms.

The TestApp accepts `--url <http-or-https-address>`, reports its native top-level window and final address through the shared protocol, and keeps the same process alive across `Navigate` commands. The shared controller supplies reserved `real-web` and `0` scenario fields only to reuse the common process envelope; they are not a reproducibility key. The TestApp does not generate semantic anchors or implement `MoveNext`; the live website and browser provider define its mutable content. Native-host mode is the default because an offscreen/compositor browser can change the native accessibility topology being measured. On Windows the executable includes a supported-OS application manifest, which Avalonia's Win32 `NativeControlHost` requires before it can create the WebView child window, and passes `--force-renderer-accessibility` through the WebView2 environment so the renderer exposes its semantic subtree before the first production query.

## 7. TestApp Process Contract

Each controlled scenario backend is a separate executable with its own normal UI bootstrap. A shared controller starts it with at least:

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

The controller owns only the process it launched and must terminate that exact process during cleanup. The declarative Mock exercises direct Backend root acquisition and receiver-centered element operations. The explicit Windows tier directly references the production Windows project, resolves a WinForms root HWND through `WindowsVisualElementBackend.Query`, verifies Direct, TopLevel, and Screen resolution, reads bounded scalar properties through the production UIA CacheRequest path, and lazily advances one child through the production Enumerator. Build-only TestApp references report their launch artifacts through MSBuild, which generates strongly named paths in the consuming project's intermediate output. The shared target resolves the app host on Windows with `.exe` and the extensionless app host on Unix; test code must not reconstruct `bin`, Configuration, target-framework, or runtime-identifier paths. The explicit real-web probe resolves the native WebView top-level window through the production Windows Backend, runs the canonical VisualQuery pipeline, requires a UIA Document, and writes the exact model-facing projection to `artifacts/webview-real-web.visual-context.txt`; `EVERYWHERE_WEBVIEW_PROBE_URL` and `EVERYWHERE_WEBVIEW_PROBE_OUTPUT` override its default URL and output path. The same WebView TestApp is cross-platform, while macOS and Linux inspection still requires composition with their production Backends.

`Everywhere.Automation.WebView.Probe` is the exploratory host for non-deterministic web journeys. Its first platform composition keeps one Avalonia NativeWebView process, one production Windows Backend, and one `VisualContext` alive while navigating each supplied URL in order. Every completed navigation produces the exact compact Agent projection plus a JSON summary containing the requested/final address, separate navigation and observation durations, published target count, retained target count, and retained turn count. With no URL arguments it visits Example Domain, Wikipedia, and GitHub. Network results are evidence for manual review and regression discovery; they are not required deterministic assertions.

```text
dotnet run --project tests/Everywhere.Automation.WebView.Probe -- https://example.com https://www.wikipedia.org
```

### 7.1 Interactive Streamable HTTP MCP Probe

`query_visual` and `read_visual_text` accept `shouldStartNewTurn=false`. Passing true completes the preceding persistent turn and begins another, matching Everywhere's delayed conversation-turn completion. Subsequent calls, including navigation between reads, share that turn. Without a persistent turn, each call owns a temporary turn that completes on success and is abandoned on failure. A failed operation inside a persistent turn does not discard earlier published targets. Session disposal releases all turns. Publication statistics count the individual build, not accumulated turn membership.

Against a freshly started server, run `pwsh -File tests/Everywhere.Automation.WebView.Probe/Verify-Retrieval.ps1 -Endpoint http://127.0.0.1:5197/mcp` to exercise temporary calls, a persistent turn spanning more than eight queries, text retrieval by an early ID, and the next-turn transition. The script then calls `diagnose_topology`, which saves `topology.json` and compares native edges before canonicalization. Native IDs and pointers are diagnostic data, never Agent target IDs. See [the recorded WebView parent conflict](Investigations/2026-09-07-WebView-Parent-Conflict.md).

In PowerShell, pass `-Addresses @('https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference', 'https://github.com/microsoft/terminal', 'https://en.wikipedia.org/wiki/Accessibility')` to add live structural/text journeys after the smoke check. Each URL gets a persistent turn. Dot-source the script with `-ConnectOnly` to initialize the connection and use `Invoke-Probe` for adaptive follow-up calls without restarting the smoke check. Full tool outputs and timings are written by the server; script summaries are observations, not golden assertions about third-party pages.

The explicit `NativeTextPagingTests` covers stable multiline page concatenation, prefix mutation, suspended UI-thread behavior, and recovery on WinForms and Avalonia. See [the native and web text results](Investigations/2026-09-07-Text-Retrieval.md) for evidence and limits.

The same executable can host a long-lived Streamable HTTP MCP endpoint for interactive Agent evaluation:

```text
dotnet run --project tests/Everywhere.Automation.WebView.Probe -- --mcp --listen http://127.0.0.1:5187
```

The MCP endpoint is `http://127.0.0.1:5187/mcp`. Registering this HTTP address in an MCP client such as Codex does not launch the executable. Start the Probe first and keep its terminal alive. A client may cache its tool registry for the lifetime of a task or connection, so adding the endpoint or starting it after a task has already begun may require reconnecting the MCP server, reloading the client, or opening a new task before the tools appear. Stopping the Probe invalidates the HTTP session and every retained visual target.

The controlled browser, production Windows Backend, and `VisualContext` are created lazily by the first `navigate` or `query_visual` call. They then remain in one server-owned state until shutdown. The host is intentionally a single-Agent diagnostic fixture: all MCP clients connected to the same process share the browser, current address, target IDs, retained turns, artifact directory, and serialized operation queue. It does not provide per-client browser or `VisualContext` isolation.

| Tool | Purpose and important parameters |
| --- | --- |
| `get_probe_status` | Reports whether the browser has started, its current address and native roots, retained target/turn counts, next ID, and artifact directory. It does not start the browser. |
| `navigate` | Loads one absolute HTTP or HTTPS address in the existing controlled native WebView process. `settleMilliseconds` may override the default accessibility propagation delay for a dynamic page. |
| `query_visual` | Runs the production Snapshot and PromptNode pipeline. Start with target `root`; later calls may use a returned integer visual element ID. `directions` accepts `all`, `parent`, `child`, `previous`, `next`, `siblings`, or `none`; `offset` pages retained members when the element exposes `observedMembers`, otherwise it remains 1; `limit` is clamped to 256; `targetTokenBudget` is an approximate renderer budget rather than a model-tokenizer guarantee. |
| `read_visual_text` | Runs the same production `VisualTextQuery` used by the Chat tool. It accepts a retained integer visual element ID, zero-based UTF-16 `offset`, and a character `limit` clamped to 16,384; pass a returned `next` value back unchanged to continue. |

A representative Agent journey is:

1. Call `get_probe_status` to identify accidental state left by another client.
2. Call `navigate` with the page under investigation.
3. Call `query_visual(target: "root", directions: "child")` for a bounded first view.
4. Follow returned IDs with narrower `query_visual` calls. When an element exposes `observedMembers`, use successive offsets; otherwise follow its returned child IDs.
5. When a result reports `moreText` or its preview is insufficient, call `read_visual_text` with that ID and continue with each returned `next` offset.
6. Navigate again to compare another page in the same process and repeat from `root`.

The live accessibility tree is mutable. A retained ID preserves its logical target identity while retained, but it does not freeze the native provider object or guarantee that the target remains available after page navigation or DOM replacement. After navigation, begin again from `root`; treat an old ID that reports unavailable or changed state the same way an Agent treats a stale file-search result. The current Probe intentionally exposes only retrieval tools. Invoke, text input, keyboard, pointer, and other Computer Use actions require their own Agent-facing semantics and are not smuggled into this diagnostic surface.

Each successful structural query writes its exact model-facing projection to a numbered `*.visual-context.txt` file, and each text page writes a numbered `*.visual-text.txt` file. Every operation appends bounded request/result metadata to `transcript.jsonl`; batch mode additionally writes `summary.json`. The default artifact directory is `artifacts/<timestamp>` beneath the built Probe executable, and `--output <directory>` selects an explicit location. Use `--limit`, `--budget`, `--settle-ms`, and `--timeout-ms` to change server defaults. Press Ctrl+C to stop the host; graceful shutdown disposes the shared `VisualContext`, Backend, controller, and exact native WebView process tree.

The default loopback binding has no authentication and must not be changed to a network-visible address without adding an explicit access-control boundary. Real-web output may contain page data and must not be published as a CI artifact without reviewing its content.

### 7.2 Agent-in-the-Loop End-to-End Evaluation

The Streamable HTTP Probe supports exploratory end-to-end evaluation in which an Agent reads the same tool descriptions and model-facing results as a production caller, chooses follow-up targets, continues long text, and navigates between live pages. This complements deterministic tests because it reveals semantic ambiguity and unstable retrieval workflows that exact assertions do not predict well.

An Agent-driven run records the URL and final address, operating system and native browser engine when available, build revision, command transcript, timings, and exact bounded outputs. Review focuses on invariants rather than a golden copy of a mutable third-party page: the Agent can locate a Document, narrower ID queries remain usable, text continuation does not silently skip content, expected `moreText`/`next` facts do not become failure status, navigation advances the revision, and every operation remains bounded.

The executable and all referenced projects must be rebuilt after implementation changes. `dotnet run --no-build` executes the already deployed output graph; a newly built library elsewhere in the repository does not replace an older copy beside the Probe executable. A stale copied dependency can therefore reproduce obsolete behavior even when the source is correct. Retain the executable build identity with artifacts and rebuild the Probe before treating a discrepancy as a product regression.

This tier remains exploratory and manual. Agent choices and public web content are not deterministic enough for required CI, while the transcript makes a useful journey reproducible enough for investigation.

### 7.3 Programmatic Unresponsive State

The shared TestApp protocol provides a test-only API that can deliberately make a supported real target unresponsive and later restore it. The API controls provider behavior; it does not change the declarative scenario tree or add a production Visual Context hook. Its illustrative command shape is:

```csharp
public enum TestAppCommandKind
{
    MoveNext,
    SetResponsiveness,
    Stop,
}

public enum TestAppResponsivenessMode
{
    UiThread,
    AccessibilityProvider,
}

public sealed record TestAppCommand(
    TestAppCommandKind Kind,
    string? TargetPath = null,
    bool? IsResponsive = null,
    TestAppResponsivenessMode? ResponsivenessMode = null);
```

The exact DTO may evolve with implementation, but the synchronization contract is mandatory:

```text
controller: SetResponsiveness(IsResponsive: false)
    -> target control thread installs the requested gate
    -> target UI/provider thread enters the blocked region
    -> target acknowledges the unresponsive state
controller: invoke the production UIA or AX query and observe its native timeout
controller: SetResponsiveness(IsResponsive: true)
    -> target control thread releases the gate
    -> target acknowledges recovery
```

The JSON-lines control reader remains on a dedicated background thread so it can release the gate while the UI or provider thread is blocked. A target must acknowledge entry only after the blocked thread has actually reached the gate, and acknowledge recovery only after that thread can make progress again. Tests use these acknowledgements rather than sleeps.

The acknowledgement uses a distinct responsiveness-changed status and echoes the resolved target, mode, and current `IsResponsive` value. An unsupported target or mode returns an error before any thread is blocked, so the controller never mistakes lack of capability for the timeout behavior under test.

The controller restores responsiveness in `finally`. If the target cannot acknowledge recovery within a bounded interval, cleanup terminates only the exact process tree owned by that controller. One failed responsiveness test must not leave a blocked TestApp or cause cleanup code to search for unrelated processes by name.

Backends advertise supported responsiveness modes and target granularity. A toolkit that can only block its UI dispatcher accepts a root or process target and reports element-scoped requests as unsupported. A custom Win32, AX, or other provider fixture may later expose a narrower accessibility-provider gate. CefSharp must not claim a releasable renderer-thread failure mode until its separate process and JavaScript lifecycle can implement the same bounded handshake.

The command sequence is part of the test configuration, not part of scenario generation. Scenario and seed still reproduce the logical UI; the reported command sequence and target path reproduce the induced runtime state within the same commit.

The first retained Windows implementation exposes coarse `SuspendUiThread` and `ResumeUiThread` commands with acknowledged `UiThreadSuspended` and `UiThreadResumed` statuses. The WinForms `input` anchor uses a test-only custom accessibility object whose Name and Value providers wait on the same controller-released gate. This is necessary because blocking the WinForms dispatcher alone does not make standard HWND/MSAA proxy property reads block; the initial native probe returned cached property snapshots in a few milliseconds while the UI thread was suspended. The custom object remains a real out-of-process UIA provider boundary and does not modify the declarative scenario. Avalonia and CefSharp currently implement the coarse UI-thread gate only; they must not claim provider-timeout coverage until their accessibility bridge is shown to enter that gate or receives an equivalent honest provider fixture.

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
- a real provider or UI thread held in a controller-releasable unresponsive state where the backend supports it;
- repeated equal-priority structures.

## 9. Assertions

`AdversarialSnapshotTests` supplies malformed relations directly through the normal Element/Enumerator contracts, independently of the declarative UI tree. Three cases cover empty containers containing useful text, shared children/self/ancestor cycles, and endless duplicates stopped by either the operation or child budget. Assertions cover unique expansion, disposal, published-ID follow-up, and release after turn eviction; they do not require a globally complete Snapshot.

For a read-only real-application probe, open the application's window and run `dotnet test tests/Everywhere.Automation.Windows.Tests --filter FullyQualifiedName~ExternalWindowProbeTests`. `EVERYWHERE_EXTERNAL_PROBE_PROCESS` selects its process name (default `Reqable`); `EVERYWHERE_EXTERNAL_PROBE_OUTPUT` optionally selects the local output directory. It never closes or modifies the external application. Three bounded Snapshot/Prompt passes check forest identity uniqueness, retained-ID querying, and identity-map return to its acquisition baseline after Snapshot disposal and turn eviction. The shared native topology diagnostic samples original COM references independently, including unidentified objects under a hard edge cap, without publishing synthetic identities. A short first-child-chain comparison uses the test project's existing legacy interop reference to compare cached and direct RuntimeIds; production interop is unchanged. Passing this probe verifies bounds and ownership, not completeness of the application's readable content or absence of all native leaks.

Mock tests may assert exact logical traversal and operation counts. Real-process tests primarily assert semantic anchors and invariants:

- the operation terminates inside configured bounds;
- platform calls, enumerated children, snapshot nodes, and snapshot text remain bounded;
- the expected core anchors remain represented or carry explicit status explaining why they are incomplete;
- no target ID silently changes meaning;
- every replacement element returned to test code is already owned by exactly one `VisualContext`;
- Element and Composite capabilities remain honest;
- partial results retain bounded status for limits, timeouts, unavailable fields, and incomplete enumeration;
- a relation timeout does not become an empty successful relation or `HasMore = false`;
- a timed-out operation is attempted once and is not automatically retried;
- repeated same-provider failures may suppress additional calls to that provider for the current Snapshot and add root status;
- unrelated providers and roots remain eligible after one provider fails;
- a later explicit query can query the provider again after the controlled target recovers;
- all Enumerators, retention batches, Contexts, captures, and Backend fixtures are released by their owners;
- disposing one ownership batch does not release an element retained by another batch;
- final retention release, Context turn eviction, and Context teardown release platform resources exactly once;
- republishing the same retained target reuses its committed Agent ID;
- committed Agent IDs are monotonic within one Context and are never silently reused or retargeted;
- merged PromptNode projection does not perform platform reads;
- final model-facing output is a `PromptNode`, native XML uses `PromptElement`, and required status survives optional-content pruning;
- Composite output preserves or summarizes status from failed source members;
- equivalent stable observations produce deterministic logical output.

Full UIA, AX, or AT-SPI tree snapshots are diagnostic artifacts, not the primary cross-version correctness oracle.

## 10. Execution Tiers

| Tier | Contents | Default execution |
| --- | --- | --- |
| Mock deterministic | Hand-written scenarios and fixed seeds | Every relevant test run |
| Mock generated | A bounded set of reported seeds | Pull requests and larger CI runs |
| Controlled TestApps | WinForms, Avalonia, and CefSharp scenario target processes | Platform integration or nightly runs |
| Native real-web Probe | Avalonia WebView host observed by a production platform Backend | Explicit/manual only |
| Agent-in-the-loop E2E | MCP-driven retrieval journeys over the native real-web Probe | Explicit/manual only |
| Provider-timeout integration | Controller-gated real UI or accessibility providers observed through production UIA or AX clients | Isolated desktop or VM only |
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

1. Project the declarative tree through Context-owned mock `VisualElement` subclasses backed by the Mock Backend.
2. Add operation counters, path-addressed failure schedules, repeated-provider failures, Enumerator metadata, retention/turn state, Backend state, and disposal tracking.
3. Run the current mechanically migrated Builder against the same canonical Mock elements used by replacement-contract tests.
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
6. Extend the shared control protocol with acknowledged responsiveness gates and add production-reader timeout/recovery tests for each honestly supported backend mode.

### Phase 5: Native Real-Web Evaluation

Future platform composition should extract only the seam selecting the production Backend and root locator. The TestApp remains cross-platform; the Probe currently owns `WindowsVisualElementBackend`. macOS requires its native AX Backend, and Linux requires an AT-SPI Backend plus native WebView validation. Compare WebView2 and WKWebView observations without requiring identical trees.

Long-text evaluation should cover Win32 and Avalonia multiline controls, reads to exhaustion, mutation, controlled provider timeout, and fetched-prefix versus returned-page cost. Native AX Value/range behavior requires macOS evidence. The alternative range/checkpoint algorithm belongs to [06-VisualQuery](06-VisualQuery.md#71-alternative-native-range-pagination); it is an evidence-gated optimization, not a temporary implementation defect.

1. Host arbitrary HTTP or HTTPS pages in the Avalonia NativeWebView TestApp without extending the deterministic scenario model.
2. Drive the TestApp through the shared process protocol and compose it with each production platform Backend.
3. Expose the production query and long-text surfaces through one Streamable HTTP MCP Probe per available platform composition.
4. Preserve bounded artifacts and transcripts for human and Agent-in-the-loop review without turning mutable webpage trees into golden assertions.

A minimal smoke scenario may be brought up on each real backend earlier if needed to keep the declarative model honest. Bulk backend coverage remains Phase 4.

The controlled process smoke tier is explicit because it opens real windows. Building its Windows test project also builds the three scenario TestApps and the separate native WebView TestApp. It can be run on an interactive Windows desktop with:

```powershell
dotnet test tests/Everywhere.Automation.Windows.Tests/Everywhere.Automation.Windows.Tests.csproj `
  --filter TestCategory=ControlledTestApp -- NUnit.ExplicitMode=Relaxed
```

This tier also retains an explicit two-MTA-thread Windows UIA compatibility probe. The threads are probe infrastructure, not the production Backend architecture. It obtains a retained root Element on one client/thread, then verifies scalar refresh and TreeWalker child navigation through the other for WinForms, Avalonia, and CefSharp. The probe waits for both native UIA clients to initialize before forcing the cross-client call so activation failure and retained-reference compatibility remain separate observations. It exercises the explicit unmanaged COM owners rather than relying on RCW lifetime.

Temporary gaps that must be closed by later stages are tracked in the repository-root `temp.md` rather than hidden behind test-only fallbacks.

## 12. Non-Goals

- A general UI framework or production abstraction.
- A user-authored JSON, XML, or YAML scenario language.
- Stable generated output across unrelated commits.
- Identical accessibility-tree shapes across platforms or toolkit versions.
- Materializing the complete logical tree before a test starts.
- Testing arbitrary third-party applications in required CI.
- Replacing focused unit tests for the merged PromptNode builder, `VisualContext`, retention/turn, and Backend behavior.

## 13. Initial Acceptance Criteria

The first useful milestone is complete when:

1. one declarative scenario containing ordinary controls, random text, and a lazy collection can be generated repeatedly from a reported seed;
2. the Mock backend exposes it through bounded parent, child, and sibling enumeration;
3. the current Weighted BFS can consume it through the canonical Mock element model;
4. a mutable scenario changes deterministically once per `MoveNext` attempt;
5. a large logical child count does not cause proportional setup allocation;
6. a failure reports enough information to rerun the same scenario and seed from the same commit.
