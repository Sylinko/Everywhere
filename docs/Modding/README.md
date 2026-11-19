# Modding Documentation

## Naming

- `Mod` is an installable extension and its lifecycle unit (`IMod`, `ModId`, `ModEngine`).
- `Everywhere.Modding.*` provides the host abstractions, runtime hosting, packaging, and MSBuild SDK.
- `Everywhere.Mods.*` contains concrete mods (`mods/Everywhere.Mods.Everything`) and samples (`mods/samples/Everywhere.Mods.Sample`).
- Package metadata uses `mod.json` and `mod-host.json`.
- `ChatPlugin` / `ModChatPlugin` represents a chat tool group backed by `Microsoft.Extensions.AI` (`AIFunction`) and the host Harness (`ToolBinding`, `FunctionCallContext`). A mod may contribute a `ModChatPlugin`, navigation pages, settings, or background services; it is not itself a `ChatPlugin`.

## Documents

Read [01 — Specification](01-Specification.md) for shared runtime rules, then the topic specification or developer guide you need.

| Document | Scope |
|---|---|
| [01 — Specification](01-Specification.md) | Identities, validation, dependency graph, collectible loading, lifecycle, and packaging contracts |
| [02 — Mod Developer Guide](02-ModDeveloperGuide.md) | Authoring, SDK setup, resources, native binaries, optional dependencies, and unloading pitfalls |
| [03 — Validation](03-Validation.md) | Verified regression suites, integration coverage, and known runtime limits on .NET 11 |
| [04 — Chat Tool Integration](04-ChatToolIntegration.md) | Harness-based `ModChatPlugin`, revocable `AIFunction` proxies, hooks, and UI detachment |
| [05 — Settings Specification](05-SettingsSpecification.md) | Mod settings persistence, UI registry, JSON metadata isolation, and migration contracts |
| [06 — Settings Developer Guide](06-SettingsDeveloperGuide.md) | Defining settings models, source-generated descriptors, migrations, and chat-tool settings UI |
| [07 — Service Boundary](07-ServiceBoundary.md) | Autofac `HostServiceContainer`, activation scopes, `IAsyncInitializer` ordering, and cache cleanup |
