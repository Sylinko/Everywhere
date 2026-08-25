# Everywhere Agent Guidelines

This file contains repository-wide rules. Keep it short enough to read before every task.
Topic-specific guidance lives under `docs/References` and must be read only when the task touches the corresponding area.

## Code of Conduct
- Never modify the code unless explicitly instructed to do so-for example, with a command such as "Start implementation". But you can still execute commands, write temporary probe code, or use other methods to gather information.

## Required References

- For Avalonia controls, Views, Presentation objects, DynamicData-backed UI collections, virtualization, or UI animation, read [`docs/References/AvaloniaViewPresentation.md`](docs/References/AvaloniaViewPresentation.md) before making changes.

## Source of Truth and Change Discipline

- Inspect the current implementation and its call sites before changing it. Design documents may lag behind the code after later refactoring and are not automatically authoritative.
- Preserve unrelated and uncommitted user changes. Do not clean up, revert, reformat, or otherwise alter files outside the task's scope.
- Do not run repository-wide or file-wide formatting when it would create unrelated diffs. Format only the code affected by the task.
- Discuss new NuGet packages or other dependencies with the user before introducing them, especially when the existing code can implement the requirement at a reasonable cost.
- Remove obsolete compatibility layers when they have no real callers and are not required by a public API, serialized format, or other compatibility boundary.

## C# Style and Type Layout

- Follow standard .NET naming conventions, including .NET capitalization of acronyms.
- Keep APIs open for future extensibility: prefer `public` when no concrete ownership, lifetime, or safety boundary requires `internal` or `private` visibility.
- Use `var` for all local variable declarations.
- Give Boolean properties, fields, parameters, and local variables a clear semantic prefix such as `Is`, `Has`, `Can`, or `Should`; private fields retain the underscore prefix, for example `_isDisposed` and `_hasCount`. Preserve established framework or API contract names whose Boolean semantics are already unambiguous, such as `Dispose(bool disposing)`.
- Prefer expression-bodied members (`=>`) for simple members.
- Prefer guard clauses and early returns over unnecessary nesting.
- Keep declarations, invocations, and expressions on one line when reasonably readable. Break only genuinely long code, and do not mechanically reflow existing code solely to enforce this preference.
- Within a type, normally order member categories as follows:
  1. properties;
  2. events;
  3. fields;
  4. methods.
- Static or readonly members may be placed earlier within their category when that improves readability. Do not mechanically reorder existing code solely to enforce this preference.
- Non-constant fields must be `private`. Constants may use the visibility appropriate to their API, including `public` or `internal`.
- Public nested types may appear at the beginning or end of their containing type. Place private nested types at the end.
- Do not retain unused positional-record parameters. They become captured state and implicitly participate in generated equality, hashing, and string formatting.
- Never use `_ = DoAsync()` fire-and-forget pattern. Use `TaskExtensions.Detach` instead.
- Avoid using `!` to suppress null warnings whenever possible. Best practices: Mark `out` parameters with the `NotNullWhenAttribute`; mark initialization methods with the `MemberNotNullAttribute`; use `if` statements to check for potential null fields in advance.
- `#region` is allowed when it genuinely improves navigation; it is not a substitute for a well-factored type. `partial class` is preferred for large types that genuinely need to be split across files.

## Architecture and Production APIs

- Optimize for the application's real ownership, call paths, and lifetime. Do not design internal APIs as generic libraries for hypothetical consumers.
- Match runtime argument validation to a real boundary. Within trusted, nullable-enabled solution code, rely on the type system and nullable analysis instead of repeating null or argument checks by default. Validate data that crosses external, serialization, reflection, plugin, native, or other boundaries where compile-time contracts do not apply.
- Prefer fewer states, objects, synchronization mechanisms, and intermediate abstractions, but never at the cost of stable identity, incremental updates, correct lifetime, or readability.
- Constructors must express a clear production purpose. Avoid ambiguous default parameters, forwarding-only constructor chains, and constructors added solely for tests.
- Do not add production methods, constructors, properties, or other hooks solely to make tests easier. Tests should use normal production entry points, mocks, reflection, or `UnsafeAccessor` where appropriate. Reconsider tests that would materially distort the production design.

## Comments and Documentation

- Write all code comments and XML documentation comments in English.
- Add necessary XML documentation to public APIs. Use `<inheritdoc />` for interface implementations and overrides when the inherited contract is sufficient.
- Preserve useful existing comments during refactoring. Do not remove documentation merely because the surrounding implementation is being rewritten.
- Document non-obvious lifetime rules, thread boundaries, state transitions, algorithms, and performance tradeoffs in enough detail for a future maintainer to understand why the design exists.
- Write technical specifications in English under an appropriate subdirectory of `docs`.

## Localization

- Everywhere uses source-generated localization APIs. Use those APIs instead of manually maintained resource accessors.
- When adding or changing localized text, edit only:
  - `src/Everywhere.Core/I18N/Strings.resx`;
  - `src/Everywhere.Core/I18N/Strings.zh-hans.resx`.
- Do not edit any other locale-specific RESX file. GitHub Actions generates or updates those translations automatically.
- Name localization keys after their current semantic meaning and owning area. Do not preserve an obsolete feature prefix after a value becomes shared.
- Prefix genuinely shared localization keys with `Common_`.

## Tests

- Name test methods in PascalCase, with underscores separating the scenario, action, and expected result. For example: `UpdateContent_WhenConditionBecomesFalse_ClearsChild`.
- Follow the repository's existing build and test practices and choose verification proportionate to the scope and risk of the change.
