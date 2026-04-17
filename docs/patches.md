# Everywhere Dependency Patching Architecture

The Everywhere project relies heavily on various large-scale third-party packages. However, these upstream dependencies frequently fall short of our specific requirements, and achieving optimal compatibility often necessitates rewriting their internal code. To address these limitations systematically, we have adopted distinct patching strategies tailored to the nature of each dependency.

## Strategy 1: Source-Level Submodule Replacement (Forking)

This is the most straightforward approach, typically reserved for large-scale, heavy-weight third-party frameworks. We fork the repository, place it in the [`3rd`](https://www.google.com/search?q=3rd) directory, and maintain our modifications on a dedicated branch.

For instance, with [`shad-ui`](https://www.google.com/search?q=3rd/shad-ui), the initial step was to prune unused references, followed by a comprehensive refactoring of core classes and styles. Although an experimental Acrylic color theme was initially attempted, it was discarded due to sub-optimal visual performance on physical devices. Instead, efforts were concentrated on refining the default monochrome (black and white) themes, significantly improving the overall UI harmony. Additionally, we streamlined its redundant logic: a triple-nested dialog builder was eliminated while preserving the original fluent API skeleton, thereby optimizing underlying execution efficiency. We also resolved all nullable warnings across the project. Upstream updates are continuously integrated via targeted cherry-pick commits.

A similar methodology was applied to [`MessagePack-CSharp`](https://www.google.com/search?q=3rd/MessagePack-CSharp). We optimized its source generator to emit `partial` classes by default, mitigating the limitation where external code could not access private fields within business logic. We fundamentally fixed generator evaluation issues in edge-case scenarios, such as explicit interface implementations, nested subclasses, and property overrides. Furthermore, we introduced a custom `OnlyIncludeKeyedMembers` attribute. MessagePack traditionally requires explicit property annotations for objects inheriting from unmodifiable base classes, otherwise throwing a missing key error. This custom attribute safely bypasses that strict constraint.

## Strategy 2: Mirror Project Shadowing

For [`semantic-kernel`](https://www.google.com/search?q=3rd/semantic-kernel), we architected a novel resolution framework. We retained the original, unmodified submodule code but created a new "mirror" project file externally to reassemble the module.

Within this mirror project, unmodified files are directly referenced using the `<Compile Include="..."/>` directive. Only the files requiring modification are substituted with our locally rewritten versions. This logic closely mirrors the `patch-package` paradigm in the Node.js ecosystem. To minimize code intrusion, the core library continues to resolve its dependency tree via the default NuGet feeds, while our localized project reference masquerades as the original NuGet reference. This elegantly resolves version isolation conflicts caused by forceful source-code takeovers.

We previously evaluated extracting the modified code into a standalone NuGet package for distribution. However, utilizing a private feed contradicts our open-source principles, and pushing customized builds to official feeds introduces repository pollution. Conversely, mounting the entire source directly into the current workspace significantly degrades GitHub Actions daily build performance. The mirror shadowing approach perfectly balances these trade-offs.

## Strategy 3: Runtime Dynamic Hooking (Legacy Iteration)

This strategy was developed as a runtime restructuring mechanism, managed within the isolated [`Everywhere.Patches`](https://www.google.com/search?q=src/Everywhere.Patches) directory. It is utilized when mirror projects are unfeasible or when deep, underlying framework behaviors must be altered on the fly. We combined Harmony, `IgnoresAccessChecksToGenerator`, and `UnsafeAccessor` to forcefully hook and mutate target memory modules.

Currently, this is primarily used to rectify random, deeply ingrained defects within the Avalonia rendering engine. The implementation in [`TextLeadingPrefixCharacterEllipsis_Collapse.cs`](https://www.google.com/search?q=src/Everywhere.Patches/Avalonia/TextLeadingPrefixCharacterEllipsis_Collapse.cs) serves as a prime example. Because Avalonia's internal `TextLeadingPrefixCharacterEllipsis.Collapse` method lacks the `virtual` modifier, standard overriding is impossible. We integrated `HarmonyLib` to inject a custom `Prefix` method, forcefully intercepting the default execution pipeline.

This interception surfaces inaccessible internal objects, such as `FormattingObjectPool`. To obtain legal calling privileges, we utilize `IgnoresAccessChecksToGenerator` to bypass C\# compile-time safety checks. Furthermore, to access completely encapsulated private fields—like the critical `_prefixLength`—we leverage the .NET 8 `[UnsafeAccessor]` attribute. This allows us to declare external references that directly extract or mutate memory pointers of restricted fields.

All interception logic is centralized within the [`Patcher.cs`](https://www.google.com/search?q=src/Everywhere.Patches/Patcher.cs) management interface. When a difficult underlying issue triggers, an interception directive is registered at the `Patcher.PatchAll()` execution point. This separation of concerns ensures that the core business logic in the main project remains pristine.

-----

## Strategy 4: Ahead-Of-Time (AOT) Static IL Weaving (Latest Iteration)

While Strategy 3 (Runtime Hooking) is powerful, injecting IL at runtime via Harmony triggers heuristic security intercepts (e.g., Windows Defender). To ensure cross-platform compliance and native CI/CD integration, we have transitioned our primary patching architecture to **Compile-Time Static IL Transplantation**.

This evolution abandons runtime injection in favor of physically rewriting the target assemblies during the MSBuild compilation pipeline using `MonoMod.Patcher`.

**Architectural Implementation:**

1.  **The Weaving Tool:** We introduced a lightweight, cross-platform CLI tool (`Everywhere.BuildTask.Patcher`).
2.  **Declarative Patching:** Patch logic is now written as standard C\# in isolated Donor projects (e.g., `Everywhere.Patches.Avalonia.Base`). Instead of Harmony's Prefix/Postfix, we utilize `[MonoModPatch]` and `[MonoModReplace]` attributes. Private members are safely mapped using `extern` or private declarations, eliminating the need for `[UnsafeAccessor]` or reflection.
3.  **Pipeline Interception:** Within the main project's `.csproj`, we hook into the `ResolveAssemblyReferences` MSBuild target. The pipeline automatically resolves the official, unmodified NuGet DLLs, executes the Weaver tool to structurally fuse our custom IL into the target binaries, and dynamically swaps the `ReferencePath` in memory before compilation proceeds.

**Outcomes:**
This approach generates perfectly standard, statically modified binaries containing zero runtime hooking overhead. It is completely transparent to our GitHub Actions CI/CD workflows and fully compatible with global code-signing pipelines (Certum for Windows, Apple Codesign for macOS). The modified third-party libraries inherit the primary application's digital signature, permanently resolving all anti-virus false positives and cross-platform distribution blockers.