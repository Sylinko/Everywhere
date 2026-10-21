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

Read the engine specification for shared runtime rules, then the relevant topic contract and author
guide. Status labels describe the workspace implementation, not a released SDK or a guarantee that
all platform and deployment gates have passed.

| Document | Scope |
|---|---|
| [01 — Specification](01-Specification.md) | Identities, dependencies, lifecycle, loading, SDK and package contracts |
| [02 — Mod Developer Guide](02-ModDeveloperGuide.md) | Authoring, host references, resources, conditional dependencies and distribution |
| [03 — Validation](03-Validation.md) | Maintained regression coverage, observed limits and release gates |
| [04 — Chat Tool Integration](04-ChatToolIntegration.md) | Implemented chat registration, SK proxies, approval hooks and author instructions |
| [05 — Settings Specification](05-SettingsSpecification.md) | Settings persistence, UI registration, cache and migration contracts; implementation and remaining limits |
| [06 — Settings Developer Guide](06-SettingsDeveloperGuide.md) | Settings model generation, opening, UI ownership and migration examples |
| [07 — Service Boundary](07-ServiceBoundary.md) | Application and activation DI ownership, native Autofac sources and service retirement |

The main specification and developer guide summarize topic boundaries and link to their full
contracts instead of duplicating them. New integration topics should follow this structure.
Implementation discoveries may require revising an agreed design. Before final merge, reconcile
contracts, examples and actual behavior, and complete or explicitly defer remaining work.
