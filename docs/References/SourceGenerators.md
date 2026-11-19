# Incremental Source Generators

Use this guide when implementing or reviewing generators, or investigating their build and IDE performance.

## Put a value model between semantic analysis and emission

Use this pipeline:

1. Find relevant declarations, preferably with `ForAttributeWithMetadataName` for attribute-driven generators.
2. Resolve symbols, aliases, inherited members, and referenced types in the semantic transform.
3. Return only the values needed by the output: names, expressions, flags, and ordered collections of value models.
4. Compare those models by value before formatting and adding source files.

An unrelated edit may still rerun semantic analysis. That is necessary: a base class, alias, attribute argument, or referenced type can change without changing the annotated declaration. The useful guarantee is that an unchanged output model stops downstream formatting and emission.

Keep `Compilation`, `SemanticModel`, `ISymbol`, `AttributeData`, and syntax nodes local to semantic analysis rather than in source-emission models. One symbol can retain much more than its own declaration. Diagnostic branches may retain source-backed locations to preserve compiler suppression and editor positioning; keep these separate from source-model equality so moving a diagnostic does not regenerate unchanged source.

A record containing `ImmutableArray<T>` does not automatically compare array contents. Give collections ordered structural equality, and include every value that affects the output. A resource's value affects its generated XML comment even when its key is unchanged.

## Match invalidation to the output unit

Keep independent files on independent pipeline branches. Avoid `Collect()` followed by a loop emitting every class when each class owns its file.

Use collection only for a real global requirement, such as duplicate RPC contract IDs. Project the smallest necessary data for that check and gate individual outputs with the result. A duplicate check must still suppress conflicting output, not merely report an error.

`CompilationProvider` is appropriate for compilation-wide discovery, such as an automatic host settings root. Project it to a comparable model before emission. Avoid combining the entire compilation into every downstream step, and avoid scanning every named type to find a known attribute.

## Cache inputs, not finished work

Parsing an additional file belongs before combining it with independent options such as the output namespace. Project analyzer options to the strings or flags actually used, rather than a fresh reference-identity options object.

Returning a completed source string and comparing it can save `AddSource` work, but it does not save generation. Place the equality boundary before expensive formatting. Prefer Roslyn's driver-owned cache over static dictionaries or custom weak caches.

Pass cancellation tokens to Roslyn APIs and check cancellation at meaningful units such as types, properties, and contracts. Let `OperationCanceledException` propagate; cancellation is not a generator error.

## Verify both reuse and correctness

Use one retained `GeneratorDriver` across edits with tracked incremental steps enabled. Name the model and formatting stages so tests can distinguish `Unchanged` semantic results from `Cached` formatting. Identical output text alone does not establish reuse.

Cover realistic changes:

- An unrelated method body changes: existing output formatting is cached.
- One annotated type changes: unrelated files remain cached.
- A base member, alias target, enum label, or descriptor child changes: the affected file updates.
- A duplicate ID appears and disappears: diagnostics and source suppression both update.
- A diagnostic moves: its current location is reported even if the valid source model is unchanged.

Compare generated source and diagnostics before and after structural refactors, then compile representative consumers. Keep regression tests for these contracts; keep timing and allocation experiments outside the permanent test suite. Avoid elapsed-time assertions.

## Read memory evidence at the retaining edge

A large workspace or generator driver is expected to own compilations. Its retained size alone does not identify a leak. Follow references to the cache entry or delegate that keeps obsolete compilations reachable, and distinguish current state from accumulated history with repeated edits.

Separate generator-owned retention from compiler or IDE caches. A small standalone driver probe can establish whether old compilations become collectible when inputs and the driver are released. Optimizing our pipeline reduces allocations and repeated work; it does not by itself repair a compiler or IDE retention bug.

## Reference

The [Roslyn incremental generator cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md) describes semantic transforms, comparable models, and incremental testing.
