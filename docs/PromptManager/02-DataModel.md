# Data Model

## 1. Prompt

Prompts are user-managed resources.

Recommended fields:

| Field | Meaning |
| --- | --- |
| `Id` | Stable GUID. |
| `Name` | Optional user-facing name. |
| `Template` | Prompt template text. |
| `CreatedAt` | Optional product metadata. |
| `UpdatedAt` | Optional product metadata. |
| `Source` | Optional metadata describing whether the prompt started from guided creation, copy, migration, or blank editing. |
| `MetadataPayload` | Optional app-owned binary payload for UI metadata such as guided recipe snapshots. |

Only `Id` and `Template` are essential for v1. `Name` is optional.

User-created prompt IDs must be non-empty GUIDs. `Guid.Empty` is reserved for the virtual default prompt and must not be inserted into `prompt.db`.

Guided creation should produce an ordinary prompt. The saved prompt must remain editable in advanced mode.

Prompt Manager should prefer opaque binary metadata in the database when metadata is not queried by SQL. C# owns the schema and serializes it with MessagePack. This keeps local storage flexible while preserving strong typed models in application code.

Prompt display name fallback:

1. if `Name` is non-empty, display `Name`
2. if the prompt has guided recipe metadata, display the selected persona name
3. otherwise display a trimmed preview of the prompt template

View code can apply final trimming and ellipsis behavior.

## 2. Default Prompt

The default prompt is built in:

1. `Guid.Empty`
2. no normal prompt row required
3. not editable
4. not deletable
5. visible as a selectable prompt
6. includes the default skill-instruction placeholder path

Missing, unselected, or empty prompt references resolve to the default prompt. Because empty prompt content is not allowed, `Guid.Empty` is not an ambiguous "blank prompt" state.

The default prompt should be optimized as the base capability and safety layer. It should include `{SkillsPrompt}` by default so normal custom prompts do not need to repeat it.

Guided and custom prompts should usually layer persona, scenario, tone, output rules, and constraints after `{DefaultSystemPrompt}`:

```text
{DefaultSystemPrompt}

# Persona
...

# Work Scenario
...
```

This keeps tool/skill instructions centralized while still allowing custom behavior.

## 3. Assistant Reference

Custom assistants should reference prompts:

```text
CustomAssistant.SystemPromptId -> Prompt.Id
CustomAssistant.SystemPromptId == Guid.Empty -> default prompt
```

The old direct prompt text field should be migrated away from the active runtime model.

If a non-empty referenced prompt is missing, the UI should show an unresolved reference state. Runtime behavior should fall back to the default prompt and record a warning.

## 4. Guided Creation Recipe

Guided creation uses recipe metadata. Recipes are not the same as prompts.

A built-in recipe definition can define:

| Field | Meaning |
| --- | --- |
| `Id` | Stable recipe ID. |
| `Name` | Display name. |
| `PersonaOptions` | Role/persona choices. |
| `ScenarioOptions` | Work scenario choices. |
| `ToneOptions` | Style/tone choices. |
| `OutputOptions` | Output format choices. |
| `TemplateFragments` | Deterministic fragments used to compose the final prompt. |
| `RequiredPlaceholders` | Placeholders expected for this recipe. |
| `RecommendedPlaceholders` | Placeholders that improve behavior but are not mandatory. |

Recipes should be deterministic and local. They are a UI authoring aid, not a separate runtime prompt format.

Prompt rows may store a MessagePack `PromptRecipeSnapshot` inside `MetadataPayload`.

Snapshot fields should include:

| Field | Meaning |
| --- | --- |
| `SchemaVersion` | Snapshot schema version. |
| `PersonaId` | Selected persona. |
| `PreferredUserName` | Optional prompt-specific way to address the user. |
| `ScenarioIds` | Selected work scenarios, max 3, no primary scenario. |
| `ToneId` | Selected tone/style. |
| `DetailLevelId` | Selected answer detail level. |
| `OrganizationId` | Selected organization style. |
| `AdditionalRequirements` | Optional user-entered constraints. |
| `IsDetachedFromRecipe` | True after advanced editing saves over the generated template. |

Runtime rendering must ignore this snapshot. It exists only to support the authoring UI and display-name fallback.

## 5. Diagnostics

Prompt diagnostics should be modeled explicitly.

Suggested shape:

| Field | Meaning |
| --- | --- |
| `Code` | Stable diagnostic code. |
| `Severity` | Info, warning, or error. |
| `Message` | User-facing message. |
| `Span` | Optional template range. |
| `Action` | Optional quick fix. |

Diagnostics are used by the editor, guided creator, and save/assign workflows.

## 6. Diagnostic Examples

Useful v1 diagnostics:

| Code | Severity | Meaning |
| --- | --- | --- |
| `EmptyTemplate` | Error | Prompt body is empty. |
| `UnknownPlaceholder` | Warning | Placeholder is not known in the current context. |
| `MissingDefaultSystemPrompt` | Info/Warning | Prompt does not reference the built-in default prompt and may omit base instructions. |
| `MissingSkillsPrompt` | Info/Warning | Prompt bypasses the default prompt and also omits skill instructions. |
| `UnresolvedReference` | Error | Assistant references a prompt that cannot be found. |
| `RecursivePlaceholder` | Error | Template expansion would recurse or exceed renderer limits. |

`MissingSkillsPrompt` should normally be derived from the expanded/default prompt relationship. If a prompt includes `{DefaultSystemPrompt}`, it should not warn about missing `{SkillsPrompt}` because the default prompt owns skill instruction inclusion.

## 7. Future Sync Resource

Future remote prompt resource path:

```text
prompts/{guid}
```

Only user prompts with non-empty GUIDs produce future remote resources. The virtual default prompt does not sync as `prompts/00000000-0000-0000-0000-000000000000`.

The remote resource is intentionally extensionless. Its body is still an app-owned binary sync envelope, not Markdown or user-editable text. The sync layer should serialize a MessagePack wrapper and payload.

The outer wrapper is sync-owned metadata, such as object type, schema version, encryption type, hashes, timestamps, and device information. The inner payload is Prompt Manager-owned data. Encryption should be represented as first-class wrapper metadata, with `None` supported initially and future end-to-end encryption added without changing the WebDAV resource model.

The official service is the priority, but the client should be designed so a user-provided WebDAV server can work when it supports RFC 6578 sync-token. WebDAV discovers resource-level changes; semantic conflict handling remains client-owned.

Prompt Manager does not expose this as an editable local file. Local prompt content remains database-backed; the WebDAV resource is only the remote synchronization representation.

## 8. Reverse References

Prompt Manager should be able to answer:

1. which assistants reference a prompt
2. whether a prompt is unreferenced
3. whether a prompt is the built-in default
4. whether a reference is unresolved

Delete and migration workflows depend on this lookup.

The same reference-query pattern should be reused by ApiKey management:

1. which assistants reference an ApiKey
2. which web search providers reference an ApiKey
3. whether an ApiKey is unreferenced

ApiKey deletion must remove both the settings entry and the secure secret. Migration should not automatically delete currently unreferenced ApiKeys because users may keep spare keys intentionally.
