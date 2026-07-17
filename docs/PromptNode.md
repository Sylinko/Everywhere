# PromptNode

`PromptNode` is Everywhere's small declarative model for building content that will eventually be sent to a language model.

It represents a prompt as a tree instead of flattening it into a `string` immediately. The tree can retain structure, priority, safe truncation boundaries, local token limits, and XML-style context while the application is still composing a result. Rendering happens only at the boundary where text is actually required.

The design is intentionally narrower than a general document framework. It is an internal, model-facing prompt tree for tool results and other generated context.

## Why it exists

Building a tool result with `StringBuilder` is easy until the result becomes large or needs to survive beyond one request. Once everything has become a string, the application no longer knows:

- which part came from which file or search result;
- which parts may be shortened safely;
- which parts are optional and should be removed first;
- which subtree must stay together;
- how to render the result again after it has been deserialized;
- how to inspect what was included or omitted by a token budget.

`PromptNode` keeps those decisions next to the content that they describe. The final text is a materialized view of the tree, not the tree itself.

The design is influenced by the same general idea behind VS Code's [`@vscode/prompt-tsx`](https://github.com/microsoft/vscode/tree/main/extensions/copilot): construct a structured prompt first and resolve its final representation later. Everywhere borrows the useful separation between construction and rendering, but keeps a much smaller data model. It does not reproduce TSX components, component state, or an asynchronous render lifecycle.

## What it is

`PromptNode` is:

- a polymorphic, serializable prompt tree;
- a common place for relative priority and rendering behavior;
- a way for a tool to return either a small text value or a rich structured result through the same type;
- a source tree that can be rendered more than once without being modified;
- a boundary between domain code that produces content and rendering code that applies escaping and token budgets.

The declaration tree is mutable because C# collection expressions, object initializers, and fluent composition are useful while constructing a result. The renderer never mutates that tree. It creates a private materialized tree for truncation, pruning, caching, and final output.

## What it is not

`PromptNode` is not:

- a chat message or a role. `System`, `User`, `Assistant`, and `Tool` messages remain part of the chat model. A prompt node can be the result payload of a tool message, but it does not define the message role;
- a general XML DOM. `PromptElement` emits lightweight XML-style structure for the model and validates element and attribute names, but there is no XML parser, namespace model, schema, or DOM manipulation API;
- a file editor, diff model, or permission system. File operations and user consent remain in the file-system plugin and its handlers;
- a tokenizer or a guarantee about a provider's exact token count. Rendering uses Everywhere's `TokenHelper` estimate to make pruning decisions;
- a component runtime. Nodes do not have `render()` callbacks, dependency injection, asynchronous children, or component state;
- an automatic summarizer. The renderer can shorten or remove declared content, but it does not invent a semantic summary;
- a universal metadata envelope. Domain metadata belongs in the surrounding tool result or in explicit element attributes. The node tree only models prompt content and the rules needed to render it.

## The node hierarchy

```text
PromptNode
├── PromptText
├── PromptTextChunk
└── PromptContainer
    ├── PromptDocument
    ├── PromptGroup
    ├── PromptTokenLimit
    ├── PromptElement
    └── PromptChunk
```

`PromptContainer` is an implementation base for ordered children. The concrete container types give those children different rendering semantics.

### `PromptNode`

The abstract base class provides:

- `Priority`, which controls which content is discarded first when a budget is exceeded;
- polymorphic MessagePack serialization through the concrete node union;
- an implicit conversion from `string` to `PromptText`;
- `ToString()`, which renders one node without a document-wide budget while still honoring local limits.

The default priority is `int.MaxValue`. A larger number means more important content. Nodes with lower priority are candidates for removal first. Priority is a pruning rule, not a relevance score and not a sorting key.

```csharp
PromptNode title = "Search results"; // Implicitly creates PromptText.
PromptNode required = new PromptText("This must remain").WithPriority(1000);
PromptNode optional = new PromptText("This may be removed").WithPriority(0);
```

### `PromptText`

`PromptText` is atomic text. It is either rendered in full or removed in full; it is never cut in the middle.

Use it for short or indivisible content such as:

- a heading;
- a file path;
- a diagnostic message;
- a continuation hint;
- a result count.

```csharp
var header = new PromptText("Found 12 matching files\n").WithPriority(1000);
```

For ordinary short text, the implicit `string` conversion is preferred. Use `new PromptText(...)` when the node needs an explicit priority or when the type is useful for readability.

### `PromptTextChunk`

`PromptTextChunk` is text that is allowed to shorten itself at declared safe boundaries before the renderer starts removing whole nodes.

Its break modes are:

| Mode | Safe boundary |
| --- | --- |
| `None` | The chunk is not shortened. It behaves like atomic text. |
| `Whitespace` | A Unicode whitespace boundary. |
| `Line` | The end of a logical line. |
| `Separator` | A caller-provided separator, configured with `BreakOn(...)`. |

```csharp
var excerpt = new PromptTextChunk(longText)
    .BreakOnWhitespace()
    .WithMaxTokens(2_000)
    .WithPriority(100);

var lines = new PromptTextChunk(fileContent)
    .BreakOnLines()
    .WithPriority(900);

var records = new PromptTextChunk(csvRecords)
    .BreakOn("\n")
    .WithPriority(500);
```

`MaxTokens` is a local ceiling for the chunk. A chunk may still be removed completely by priority pruning if its enclosing budget cannot fit it. A chunk with `BreakMode.None` is never silently cut in half.

This is the node used by file-reading output for long logical lines. A very long line can be shortened without causing the renderer to skip the entire line as a separate item.

### `PromptContainer`

`PromptContainer` owns an ordered `Children` list and exposes the common construction surface:

- collection expressions and object initializers through `Add`;
- the `Children(...)` fluent extension;
- `PassPriority`, which controls whether children participate directly in the parent's priority scope;
- `IReadOnlyList<PromptNode>` enumeration.

It is normally not used directly. Choose one of its concrete subclasses to state the intended behavior.

Null children are ignored by `Add` and by the constructors that accept `PromptNode?`. This makes conditional composition convenient:

```csharp
PromptNode? diagnostics = includeDiagnostics
    ? new PromptElement("diagnostics", diagnosticText)
    : null;

PromptDocument document = [
    "Main answer\n",
    diagnostics
];
```

### `PromptDocument`

`PromptDocument` is the explicit root for a document-wide render. It provides:

```csharp
PromptRenderResult result = document.Render(maxTokenCount: 40_000);
```

The result contains the final content, the estimated token count, source nodes that were included, source nodes that were omitted, and text chunks that were shortened.

`PromptDocument` is not a mandatory wrapper for every result. A tool may return any `PromptNode`, including a `PromptTokenLimit`, `PromptElement`, or `PromptText`. The unbounded node render path materializes that node directly; it does not manufacture a synthetic document around it.

Use a `PromptDocument` when the caller needs a document-wide budget and `PromptRenderResult` metadata. Use a plain node when the caller only needs a structured result that will be rendered at the provider boundary.

### `PromptGroup`

`PromptGroup` is a logical grouping with no output of its own. It introduces a priority boundary without adding markup.

```csharp
var firstFile = new PromptGroup().Children(
    new PromptText("a.cs\n").WithPriority(100),
    new PromptText("match in a.cs\n").WithPriority(90));

var secondFile = new PromptGroup().Children(
    new PromptText("b.cs\n").WithPriority(100),
    new PromptText("match in b.cs\n").WithPriority(90));

PromptDocument document = [firstFile, secondFile];
```

A group is not atomic. Its descendants may still be pruned independently. If the complete subtree must be kept or removed as one unit, use `PromptChunk` instead. When sibling candidates have the same priority, their direct children provide the tie-breaker before the renderer descends into the selected group.

`WithPassedPriority()` can make a logical container transparent to its parent's priority scope. `PromptElement` is transparent by default because its tags are structural rather than independent content.

### `PromptTokenLimit`

`PromptTokenLimit` gives a subtree an explicit local token ceiling. It emits no markup. Local limits are applied before the document-wide budget, and nested limits are applied from the inside out.

```csharp
var result = new PromptTokenLimit(8_000)
{
    new PromptText("Search summary\n").WithPriority(1000),
    new PromptTextChunk(allMatches)
        .BreakOnLines()
        .WithPriority(100)
};
```

This is useful at tool boundaries. A file search can limit its own output to 40,000 tokens even when it is later composed into a larger prompt. A caller may still apply a tighter document budget afterward.

`PromptTokenLimit(0, ...)` is valid and means that no child content may survive. A negative limit is rejected.

### `PromptElement`

`PromptElement` emits an XML-style element around its surviving descendants:

```csharp
var file = new PromptElement(
    "file",
    new PromptText($"Path: {path}\n"),
    new PromptTextChunk(fileContent).BreakOnLines())
    .Attribute("path", path)
    .Attribute("language", language);

Console.WriteLine(file.ToString());
```

Example output:

```xml
<file path="src/Program.cs" language="csharp">...
</file>
```

Element and attribute names are validated as XML names. Attribute values are converted with invariant culture. Text and attributes inside an element are escaped, so `<`, `&`, and quotes cannot accidentally change the structure.

Top-level `PromptText` is deliberately not escaped. This permits a root prompt to contain literal model instructions. Put untrusted or structurally significant text inside a `PromptElement` when XML-style escaping is required.

Elements pass priority by default. Their opening and closing tags are structural wrappers; when all descendants are pruned, the empty element is omitted rather than emitted as an empty tag pair. Wrap an element in `PromptChunk` when the complete element must remain intact.

`PromptAttributeCollection` is a supporting type, not a `PromptNode`. It stores validated attribute names and invariant-culture string values for a `PromptElement`.

### `PromptChunk`

`PromptChunk` is an atomic subtree. The renderer keeps or removes the complete subtree as one unit, without emitting any wrapper of its own.

```csharp
var completeFile = new PromptChunk(
    new PromptElement("file", new PromptTextChunk(fileContent).BreakOnLines()).Attribute("path", path))
    .WithPriority(50);
```

This is appropriate when a partial block would be misleading, for example:

- a file block whose header and body must agree;
- a complete tool result with a checksum or delimiter;
- a structured example that should not be split between alternatives.

Do not use `PromptChunk` merely for grouping. It intentionally prevents useful partial pruning.

## Constructing a tree

The same tree can be created using ordinary C# object initializers, collection expressions, or fluent extensions.

### Collection expression

```csharp
PromptDocument document =
[
    new PromptElement(
        "context",
        new PromptText("Relevant files:\n").WithPriority(1000),
        new PromptTextChunk(fileContent)
            .BreakOnLines()
            .WithPriority(100))
        .Attribute("source", "file-system"),
    new PromptText("\nAnswer the user's question using the context above.")
];
```

### Object initializer

```csharp
var document = new PromptDocument
{
    "System instructions\n",
    new PromptElement("user_context", "..."),
    optionalNode
};
```

### Fluent construction

```csharp
var document = new PromptDocument()
    .Children(
        "System instructions\n",
        new PromptElement("user_context", "...")
            .Attribute("kind", "workspace"),
        optionalNode);
```

The fluent methods return the same concrete node, so the type remains available for further configuration:

```csharp
var node = new PromptTextChunk(text)
    .BreakOnWhitespace()
    .WithMaxTokens(1_000)
    .WithPriority(200);
```

## Rendering and token budgets

There are two intentionally different rendering paths.

### Rendering one node

`PromptNode.ToString()` uses `PromptNodeRenderer` to render one node without a document-wide budget. Local `PromptTokenLimit` and `PromptTextChunk.MaxTokens` still apply.

```csharp
PromptNode toolResult = new PromptTokenLimit(4_000)
{
    new PromptText("Result:\n").WithPriority(1000),
    new PromptTextChunk(largeResult)
        .BreakOnWhitespace()
        .WithPriority(100)
};

string modelText = toolResult.ToString();
```

The renderer materializes the supplied node directly. There is no implicit `PromptDocument` wrapper, and the source tree is not changed by pruning.

### Rendering a document with a global budget

```csharp
PromptRenderResult rendered = document.Render(40_000);

string content = rendered.Content;
int estimatedTokens = rendered.TokenCount;
IReadOnlyList<PromptNode> omitted = rendered.OmittedNodes;
IReadOnlyList<PromptTextChunk> shortened = rendered.TruncatedNodes;
```

The renderer performs the following stages:

1. Materialize the declaration tree into a disposable render tree.
2. Escape text that is inside `PromptElement` nodes.
3. Shorten `PromptTextChunk` values at their declared safe boundaries.
4. Apply nested `PromptTokenLimit` scopes from the innermost scope outward.
5. Apply the document-wide budget by removing lower-priority content first.
6. Render the remaining tree and collect source-node information.

The declaration tree remains available for a second render with a different budget. The renderer's private materialized nodes are never serialized.

Lower priority numbers are discarded first. Equal-priority candidates compare the lowest priority among their direct children; declaration order is used when that tie-breaker is also equal. The renderer then descends into the selected non-atomic container and repeats the same local comparison. A required atomic node that cannot fit may cause rendering to fail instead of being silently corrupted.

## Serialization and chat history

`PromptNode` is a MessagePack-polymorphic union. When a tool result contains a node, the concrete type and its properties are persisted rather than only the rendered string.

This distinction matters in chat history:

```text
durable chat history
    FunctionResultContent(Result = PromptNode tree)
                         │
                         ├── persisted as a structured node union
                         └── rendered to a temporary string for the provider
```

`ChatHistoryBuilder` preserves the structured result in the durable history and creates a provider-facing copy containing the rendered text. This keeps local limits, priorities, elements, and truncation policies available for later replay or compression. Calling `ToString()` while constructing the durable result would flatten the tree too early and lose that information.

The private `MaterializedNode` hierarchy is an implementation detail. It contains parent links, caches, escaped text, and removal state; it is never part of the serialized prompt model.

## A file-system example

The file-system plugin returns nodes instead of eagerly formatted strings. A simplified read result looks like this:

```csharp
private static PromptNode BuildReadOutput(string path, IReadOnlyList<string> lines)
{
    var output = new PromptTokenLimit(40_000)
    {
        $"File: `{path}`:\n"
    };

    for (var index = 0; index < lines.Count; index++)
    {
        output.Add(
            new PromptTextChunk($"{index + 1}: {lines[index]}\n")
                .BreakOnWhitespace()
                .WithPriority(1000 - index));
    }

    output.Add(
        new PromptText("\n[More content is available. Continue with the next offset.]\n")
            .WithPriority(int.MaxValue));

    return output;
}
```

The header is atomic, each logical line can cooperate with the token budget, and the continuation hint remains while lower-priority content lines are pruned. PDF page information can be represented in the line text or in the surrounding tool result; PDF parsing itself is a file-handler concern, not a `PromptNode` concern.

The same pattern works for search results:

```csharp
var output = new PromptTokenLimit(40_000)
{
    $"Found {matches.Count} matches.\n"
};

foreach (var file in files)
{
    var block = new PromptElement(
        "file",
        new PromptText($"Path: {file.Path}\n"),
        new PromptTextChunk(file.Content).BreakOnLines())
        .Attribute("path", file.Path);

    output.Add(new PromptChunk(block).WithPriority(file.Priority));
}
```

Here each file block is atomic, so the renderer never leaves a partial file section behind.

## Recommended usage rules

1. Return `PromptNode` from code that produces model-facing content.
2. Use ordinary strings or implicit `PromptText` for small, indivisible values.
3. Use `PromptTextChunk` only when a safe truncation boundary is known.
4. Put a `PromptTokenLimit` at a tool or subsystem boundary that must control its own output size.
5. Use `PromptElement` for model-visible structure and attributes, not as a general-purpose XML document API.
6. Use `PromptChunk` only when partial output would be invalid or misleading.
7. Use `PromptGroup` for a priority scope, not for all-or-nothing behavior.
8. Keep nodes structured until the provider boundary. Avoid interpolating a complete node into a string during construction.
9. Render a `PromptDocument` explicitly when the caller needs a global budget or `PromptRenderResult` metadata.
10. Do not add a new node type until it introduces a distinct rendering, pruning, or serialization rule.

## Relationship to VS Code `prompt-tsx`

The resemblance to VS Code's prompt system is deliberate but limited:

| Concern | Everywhere | VS Code prompt-tsx |
| --- | --- | --- |
| Construction | C# nodes, collection expressions, object initializers, fluent extensions | TypeScript/TSX prompt components |
| Structure | Serializable data tree | Component tree that is resolved during rendering |
| Text sizing | `PromptTextChunk`, `PromptTokenLimit`, and `Priority` | Renderer sizing primitives such as `PromptSizing` and component-level sizing behavior |
| Model structure | `PromptElement` emits XML-style tags | Components such as message and prompt elements compose provider input |
| Persistence | Prompt nodes can be stored in chat history through MessagePack | Prompt components are generally assembled for a request |
| Message roles | Owned by the surrounding chat model | Often represented by dedicated prompt/message components |
| Runtime | No component lifecycle or async render callbacks | Components can implement render logic and receive render state |

The important lesson is the separation of a prompt declaration from its final rendering. The implementation should not grow into a second UI framework merely because the tree resembles TSX. If a future requirement needs conditional or stateful prompt components, that can be introduced above this small node layer rather than added to every existing node.

## Summary

`PromptNode` is a compact prompt AST with rendering policy attached to the content it governs:

- `PromptText` preserves atomic text;
- `PromptTextChunk` defines safe shortening;
- containers express scope and structure;
- `PromptTokenLimit` controls local budgets;
- `PromptElement` adds model-visible markup;
- `PromptChunk` preserves all-or-nothing subtrees;
- `PromptDocument.Render(...)` applies a global budget and returns structured render information;
- `ToString()` provides a convenient provider-facing representation without destroying the source tree.

The central rule is simple: build a tree while the content still has meaning, and render it only when text is actually required.
