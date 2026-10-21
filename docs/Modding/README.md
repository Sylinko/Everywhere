# Modding Documentation

## Naming

`Mod` is one installable extension and its lifecycle identity (`IMod`, `ModId`, `ModEngine`).
`Everywhere.Modding.*` contains the host contracts, runtime, packaging and SDK infrastructure.
`Everywhere.Mods.*` contains concrete extensions and samples; `Mods` and `mods` name collections
and source directories. Mod metadata uses `mod.json` and `mod-host.json`.

`ChatPlugin` remains a chat-tool group and `KernelPlugin` remains the Semantic Kernel concept.
A mod may contribute a `ModChatPlugin`, a page or other application features; it is not itself
a chat-tool group. Existing built-in/MCP settings and their legacy storage paths keep their names.

## Documents

| Document | Scope |
|---|---|
| [01 — Specification](01-Specification.md) | Runtime, lifecycle, loading, SDK and package contracts |
| [02 — Mod Developer Guide](02-ModDeveloperGuide.md) | Authoring, host references and distribution |
| [03 — Validation](03-Validation.md) | Regression evidence, observed limits and release gates |
| [05 — Settings Specification](05-SettingsSpecification.md) | Settings persistence, UI ownership and migrations |
| [06 — Settings Developer Guide](06-SettingsDeveloperGuide.md) | Settings models, persistence and presentation |
| [07 — Service Boundary](07-ServiceBoundary.md) | Application and activation DI ownership |
