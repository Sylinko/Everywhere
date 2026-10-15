# Plugin Packaging References

This reference records relevant behavior in mature mod systems and the implications for Everywhere's plugin package design. It is not a package schema or an implementation specification. Sources were checked on 2026-10-12. Branch links can change; BepInEx `v5-lts`, BepInEx `master`, and tModLoader `stable` are identified separately below.

## BepInEx: Runtime Metadata

BepInEx `v5-lts` declares plugin identity in `BepInPlugin`: a stable GUID string, a display name, and a `System.Version`. Dependencies use the other plugin's GUID and hard/soft flags. Its versioned dependency constructor expresses a minimum version and selects a hard dependency. Incompatibility and process filters are separate attributes. These are runtime plugin relationships, not NuGet package references. [BepInEx v5 attributes](https://github.com/BepInEx/BepInEx/blob/v5-lts/BepInEx/Contract/Attributes.cs)

The `master` implementation uses `SemanticVersioning.Range` for dependency version constraints. Do not copy a current documentation example into a v5 SDK without checking the actual referenced binary: the source contracts differ. This is a branch comparison, not a claim that `master` behavior is available in the stable v5 release. [BepInEx master attributes](https://github.com/BepInEx/BepInEx/blob/master/BepInEx.Core/Contract/Attributes.cs)

**Design implications:** Keep identity independent of display name. Separate required dependencies, optional integration, and explicit incompatibility. Generate the package metadata from the plugin project so identity and version do not require repeated manual edits. Runtime compatibility constraints should not be confused with the exact versions used for one build.

## Thunderstore: Distribution Metadata

A Thunderstore package is a standard ZIP with `manifest.json`, `README.md`, and a 256-by-256 `icon.png` at its root. `CHANGELOG.md` is optional. The manifest contains `name`, `version_number`, `website_url`, `description`, and `dependencies`. Dependency strings identify repository packages as `team-package-version`; mod managers install those dependencies. This is a package distribution contract. It does not describe CLR assembly binding or BepInEx plugin GUIDs. [Thunderstore package requirements](https://wiki.thunderstore.io/mods/creating-a-package)

The official packaging guide directs authors to loader-specific instructions. Consequently, the generic Thunderstore ZIP requirements should not be mistaken for a universal managed/native file layout. [Thunderstore loader-specific packaging](https://wiki.thunderstore.io/mods/packaging-your-mods)

**Design implications:** A conventional archive, readable metadata, and a README make local installation and automated distribution easier. Keep repository location separate from runtime plugin identity so the same artifact can be distributed through a website, a release attachment, or a future catalog. Distribution dependency selection still needs a separate runtime compatibility check.

## tModLoader: Build Inputs and Package Contents

tModLoader's `.tmod` is a custom binary container, not a renamed ZIP. It contains assemblies, resources, libraries, and other packaged files, with a header and file table. Entries can be compressed individually. Its format illustrates the usefulness of one distributable artifact, but Everywhere does not need a custom container to obtain that benefit. [Official `.tmod` format](https://github.com/tModLoader/tModLoader/wiki/.tmod-File-format)

The 1.4 documentation describes `build.txt` as a source-side, line-oriented build configuration. `dllReferences` identifies non-mod managed libraries stored under `lib/`; `modReferences` identifies required mods; `weakReferences` identifies optional mods available for compilation. `sortBefore` and `sortAfter` describe ordering. `side` concerns client/server synchronization, not Windows/macOS/Linux support. `buildIgnore` controls exclusions. [Official build properties](https://github.com/tModLoader/tModLoader/wiki/build.txt)

The load-order documentation distinguishes mod ordering from content initialization. It allows a `sortBefore` entry to override the ordinary ordering implied by a mod reference. That is tModLoader's lifecycle policy; it should not be copied as permission to violate Everywhere's validated assembly dependency order. [Official loading steps](https://github.com/tModLoader/tModLoader/wiki/Mod-Loading-Steps)

The cross-mod guide supports minimum versions such as `ExampleMod@2.0`. Optional static references need additional treatment: code separation, `JITWhenModsEnabled`, and sometimes `ExtendsFromMod`. A runtime presence check inside the same method is insufficient when JIT compilation needs to resolve the missing reference. The guide requires a fresh process when testing absence, because disabling a mod alone can produce a false positive. [Official cross-mod guide](https://github.com/tModLoader/tModLoader/wiki/Expert-Cross-Mod-Content)

**Design implications:** Distinguish build inputs from generated runtime metadata. Separate lifecycle ordering preferences from actual assembly dependency edges. Prefer selecting optional/platform integration assemblies before loading over making every author understand conditional type scanning and JIT behavior. Do not adopt `build.txt` syntax merely because it is established: an open upstream proposal requests a schema-backed structured replacement, illustrating an existing usability problem rather than an implemented feature. [Upstream structured metadata proposal](https://github.com/tModLoader/tModLoader/issues/4414)

## tModLoader: Loading and Unloading Boundaries

The checked `stable` source loads declared managed libraries and the entry DLL from streams into a collectible context per mod. Unloading clears references, requests unloading of all tracked mod contexts, and observes weak references after garbage collection. This establishes unloading machinery, not guaranteed collection or independent single-mod unloading. [Stable assembly manager](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/Core/AssemblyManager.cs)

That class has no `LoadUnmanagedDll` override or RID selector. These sources do not establish a cross-platform native package convention for mods.

**Design implications:** Package ownership, assembly ownership, and release of native state are different concerns. A lifecycle callback named `Unload` cannot itself certify that an assembly context was collected. Native library selection, search paths, callbacks, background threads, and unloading policy require an explicit Everywhere contract and validation rather than an inference from a managed directory name.

## tModLoader: Type Filtering and Eager JIT

The stable implementation has two distinct filters:

1. `ExtendsFromMod` stores mod names on a class. The assembly manager enumerates metadata types in `MetadataLoadContext`, checks the attribute before traversing base classes, declaring types, and interfaces, and materializes only accepted runtime types. Its public `GetLoadableTypes` returns that filtered cache; ordinary runtime `Assembly.GetTypes()` bypasses it. [Attribute definition](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/ExtendsFromModAttribute.cs), [type filtering implementation](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/Core/AssemblyManager.cs)
2. `PreJITFilter.ShouldJIT` asks every `MemberJitAttribute` on a member and requires all answers to be true. `NoJIT` always returns false; `JITWhenModsEnabled` requires every named mod to satisfy `ModLoader.HasMod`. These attributes target classes, constructors, methods, and properties. They do not set `NoInlining`, rewrite IL, or intercept the CLR's compiler. Their scope is the host's own eager-JIT pass. [Stable JIT filters](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/JITFilters.cs)

Selected methods with bodies are passed to `RuntimeHelpers.PrepareMethod`. Abstract methods and methods containing generic parameters are excluded. The pass does not enumerate future generic instantiations. Present weak dependencies are real context links, not weak CLR references. [Stable assembly manager](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/Core/AssemblyManager.cs)

Instantiation precedes the eager-JIT pass: the main loader creates mod instances and populates its lookup before invoking `ModContent.Load`. [Stable main loader](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/ModLoader.cs)

`ModContent.Load` starts `JITModsAsync`, then performs content autoloading and `Mod.Load`/`OnModLoad` callbacks while that task runs. It awaits completion before resizing and setup. Mod preparation is awaited sequentially, while method preparation within a mod uses parallel tasks. Therefore this is not validation completed before all plugin code executes. [Stable content loading](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/ModContent.cs)

**Design implications:** Skipping host-requested compilation does not make later invocation safe, prevent normal CLR JIT, or establish an inlining boundary. Type filtering and method preparation solve different problems; skipping a method does not make its missing base type loadable. Eager JIT can expose some binding errors sooner, but cannot prove all generic, reflection, native, or data-dependent paths valid. Optional integration selected into the active graph must participate in unloading relationships. For Everywhere, selecting separate integration assemblies remains easier to validate than reproducing this attribute protocol throughout every reflection consumer.

## tModLoader: Distribution and Versions

The 1.4 workflow integrates building and publishing through Steam Workshop. A workshop item can supply different builds for stable and preview tModLoader versions, and the host selects the appropriate build. The guide also distinguishes a mutable display name from an internal name used by the loader and other mods, and discourages changing an established internal name. It warns that broad source-folder packaging can include unintended large files. These are distribution/version-selection capabilities, not operating-system asset selection rules. [Official Workshop guide](https://github.com/tModLoader/tModLoader/wiki/Workshop)

**Design implications:** One user-facing plugin can have several build variants. Keep plugin identity stable while allowing presentation changes. Let the SDK explicitly select distributable build outputs and resources instead of packaging an entire source directory by default. Distribution transport and platform selection should remain independent so local installation uses the same validated artifact as catalog installation.

## Questions These Systems Do Not Settle for Everywhere

- The exact directory names and whether platform variants replace an entry assembly or add a separate adapter assembly.
- The supported runtime identifiers, fallback order, minimum OS versions, and handling of ambiguous matches.
- Native-library ownership, transitive native dependencies, and which integrations require a restart for replacement.
- How generated file hashes and assembly identities combine with author-declared compatibility ranges and host-provided dependencies.
- How an installed package version is staged, activated, rolled back, and retired while a previous version still has live references.

These are design choices for Everywhere. The references support separating these concerns; they do not establish a ready-made schema that guarantees collectible loading.
