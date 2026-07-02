# Implementation Plan

## 1. Phase 0: Preconditions and Audit

Tasks:

1. confirm SettingsEngine branch is merged
2. confirm SettingsEngine runs migrations and binding in `AsyncInitializerIndex.Settings`
3. audit assistant prompt fields and prompt renderer entry points
4. define prompt diagnostics codes
5. define guided creation recipe shape
6. update the built-in default prompt so it contains skill/tool instruction inclusion and remains a good base for custom personas
7. define `Guid.Empty` as the default prompt reference

Acceptance criteria:

1. Prompt Manager no longer depends on `IConfiguration` details
2. default prompt identity is `Guid.Empty`
3. default prompt is suitable as a base layer for guided and custom prompts
4. migration inputs are known

## 2. Phase 1: Prompt Domain

Tasks:

1. add prompt model backed by isolated `prompt.db`
2. add prompt service
3. add built-in default prompt provider
4. add CRUD operations
5. add reverse reference service
6. add shared prompt template parser
7. update renderer to use parser-backed expansion
8. add renderer adapter for preview and picker

Acceptance criteria:

1. prompts can be created and edited independently of assistants
2. default prompt is visible but not editable/deletable and is not stored in `prompt.db`
3. prompt rendering matches chat rendering semantics
4. editor, diagnostics, preview, and runtime rendering share placeholder parsing rules

## 3. Phase 2: Assistant References and Migration

Tasks:

1. add prompt reference to assistant model
2. update assistant runtime resolution
3. migrate old assistant prompt strings
4. deduplicate migrated prompt bodies
5. map default prompt content to `Guid.Empty`
6. add unresolved reference handling
7. remove old direct prompt JSON fields only after prompt DB import succeeds

Acceptance criteria:

1. existing assistant behavior is preserved
2. assistants no longer use active direct prompt text
3. non-empty missing references have clear diagnostics and fall back to default prompt

## 4. Phase 3: Guided Creation

Tasks:

1. add recipe model
2. add built-in persona options
3. add built-in work scenario options
4. add tone and output format options
5. compose deterministic templates from selections, starting with `{DefaultSystemPrompt}` by default
6. show preview and diagnostics in the create flow
7. allow switch to advanced editing

Acceptance criteria:

1. beginners can create a useful prompt without editing raw template text
2. saved guided prompts are normal prompts
3. guided templates inherit default skills/tool guidance through `{DefaultSystemPrompt}`
4. warnings do not block intentional advanced use

## 5. Phase 4: Advanced Editor and Preview

Tasks:

1. add Prompt Manager page
2. add navigation placement after the custom assistant page
3. add prompt list/search, including pinned built-in default prompt
4. add empty selected-prompt state
5. add read mode with rendered preview, raw template tab, references panel, and top diagnostics
6. add edit mode with name/template editing, save/cancel actions, and live rendered preview
7. add placeholder highlighting in both raw read-only template and editable template
8. add live rendered preview
9. add incremental inline synchronization for preview updates
10. add placeholder reference list with color markers
11. add diagnostics panel

Acceptance criteria:

1. users can create, edit, preview, and delete prompts
2. preview refreshes time-sensitive placeholders
3. diagnostics identify common prompt issues
4. opening the page does not auto-select a prompt
5. the default prompt is visible but not editable or deletable
6. selected prompt content uses responsive layout: only the selected-prompt body changes from side-by-side to vertical scrolling at narrow widths
7. AvaloniaEdit highlights only placeholders and does not apply Markdown highlighting
8. placeholder colors match between editor, raw template, preview, and placeholder reference list
9. empty template blocks save; other diagnostics do not block save

## 6. Phase 5: Prompt Picker

Tasks:

1. add global shortcut setting
2. add picker window
3. add search and preview
4. render selected prompt with picker context
5. paste rendered content into the active app

Acceptance criteria:

1. shortcut opens prompt picker
2. picker pastes rendered prompt content
3. picker works independently of assistant editing

## 7. Phase 6: Delete Safety and References

Tasks:

1. show reference counts
2. show assistant references where practical
3. block default prompt deletion
4. navigate from a prompt assistant reference with `MainViewNavigateMessage.ToCustomAssistant(assistant.Id)` and select that assistant
5. warn before deleting referenced prompts
6. reset assistant references to the built-in default prompt after confirmed referenced-prompt deletion
7. add unreferenced prompt cleanup affordances
8. add ApiKey reverse reference lookup for assistants and web search providers
9. fix ApiKey deletion so it also calls secure-secret deletion
10. keep currently unreferenced ApiKeys unless the user explicitly deletes them

Acceptance criteria:

1. users can understand prompt usage before deletion
2. deleting prompts does not silently break assistants
3. deleting ApiKeys does not leave the secret behind
4. unreferenced ApiKeys are visible as unreferenced but are not auto-deleted by migration
5. deleting a referenced prompt intentionally falls references back to `Guid.Empty`

## 8. Phase 7: Tests

Required tests:

1. prompt CRUD
2. default prompt behavior
3. assistant prompt migration
4. prompt deduplication
5. prompt rendering
6. diagnostics for unknown placeholders
7. diagnostics for missing default prompt
8. diagnostics for missing skills placeholder only when bypassing default prompt
9. guided recipe generation
10. guided-to-advanced editing
11. prompt picker rendering
12. reverse reference lookup
13. delete safety
14. ApiKey delete removes the secure secret
15. ApiKey reverse references include assistants and web search providers
16. unreferenced ApiKeys survive migration
17. Prompt Manager page opens with empty selection and empty content
18. default prompt appears pinned and blocks edit/delete
19. referenced prompt deletion resets assistant prompt references to default
20. reference navigation selects the target assistant
21. parser token spans support editor highlighting and diagnostics
22. preview inline synchronizer updates changed placeholder values without full rebuild when segment keys are stable

## 9. Deferred Work

Deferred:

1. cloud sync
2. WebDAV/RFC 6578
3. E2EE
4. prompt sharing/import format
5. AI-assisted prompt generation
6. conflict resolution UI
