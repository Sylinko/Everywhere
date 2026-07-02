# Prompt Manager: Overview

## 1. Purpose

Prompt Manager makes prompts first-class resources in Everywhere.

Today, assistant prompts are stored as direct text fields inside assistant configuration. The new model moves prompt content into a dedicated prompt domain. Assistants reference prompts by ID, users can manage prompts directly, and prompts can later be synchronized as app-owned resources.

This document focuses on Prompt Manager itself. SettingsEngine is treated as a completed prerequisite, including its `IAsyncInitializer` lifecycle.

## 2. Product Scope

Prompt Manager should provide:

1. a dedicated page for all prompts
2. create, edit, copy, delete, and search operations
3. beginner-friendly prompt creation through guided choices
4. advanced template editing for power users
5. live rendered preview
6. warnings and suggestions for common prompt issues
7. assistant prompt reference management
8. reverse reference lookup before delete
9. a global prompt picker that pastes rendered prompt content

The feature should help both new users and advanced users. A new user should be able to create a useful prompt by choosing a persona, work scenario, tone, and output style. An advanced user should be able to edit the full template directly.

## 3. Fixed Decisions

1. Prompt identity is a GUID.
2. `Guid.Empty` is the built-in default prompt reference.
3. The default prompt is virtual. It is not stored in `prompt.db`, editable, deletable, or synchronized as a normal prompt.
4. Custom assistants store prompt references, not prompt text.
5. Prompt template rendering reuses the existing prompt renderer.
6. The prompt picker pastes rendered content, not raw template text.
7. User prompts are stored in an isolated local database, `prompt.db`.
8. Future sync is official-service first but should remain compatible with WebDAV servers that support RFC 6578 sync-token.
9. Beginner creation produces a normal prompt. Users can switch to advanced editing at any time.
10. Future sync resources use extensionless WebDAV paths such as `prompts/{guid}`, but local Prompt Manager does not expose editable prompt files.
11. The Prompt Manager page is a standalone main page placed after the custom assistant page in navigation.
12. Opening the Prompt Manager page does not select a prompt automatically. The content area shows the same style of empty state used by the custom assistant page.
13. The built-in default prompt is visible in the prompt list, pinned at the top, and marked as built-in/default, but cannot be edited or deleted.
14. Page-level diagnostics are shown at the top of the selected prompt content area and are not dismissible.
15. Prompt delete is allowed after strong confirmation. If the deleted prompt is referenced by assistants, those references are reset to the built-in default prompt.

## 4. User Workflows

Main workflows:

1. create a prompt from guided choices
2. create a blank advanced prompt
3. preview rendered prompt content
4. fix prompt warnings before saving or assigning
5. assign a prompt to an assistant
6. summon prompt picker and paste rendered content into another app
7. inspect references before deleting a prompt
8. navigate from a prompt reference list to the referencing assistant

## 5. Non-goals

The first Prompt Manager implementation should not include:

1. SettingsEngine implementation
2. WebDAV/RFC 6578 implementation
3. cloud-sync conflict resolution
4. end-to-end encryption
5. direct editing of prompt resources from a synced folder
6. automatic text merge for prompt conflicts
7. AI-generated prompt authoring unless added as a later feature

The guided creator should be deterministic and understandable first. AI-assisted prompt generation can be layered on later.

## 6. Dependencies

Prompt Manager depends on:

1. SettingsEngine for stable settings load/save and post-settings migration ordering
2. existing prompt template rendering
3. assistant configuration models
4. platform text insertion/clipboard support for the picker

## 7. Document Map

| Document | Purpose |
| --- | --- |
| `01-Overview.md` | Product scope, fixed decisions, and workflows. |
| `02-DataModel.md` | Prompt, recipe, diagnostics, assistant references, and future sync shape. |
| `03-CreationExperience.md` | Beginner guided creation and advanced mode transition. |
| `04-EditorPreviewDiagnostics.md` | Advanced editor, live preview, placeholder handling, and warning rules. |
| `05-MigrationPlan.md` | Migration from assistant prompt strings to prompt resources. |
| `06-ImplementationPlan.md` | Ordered implementation phases and tests. |
| `07-PromptPageDesign.md` | Prompt Manager page layout, actions, references, and responsive behavior. |
