# Strategy Engine Spec: Skills v1

## 1. Purpose

Skills v1 is a lightweight local registry for `SKILL.md` resources.

It is not a second Strategy Engine. It does not recommend actions, evaluate visual context, run preprocessors, auto-select skills, or convert every `SKILL.md` into a Strategy. Its purpose is to make installed skills discoverable, user-manageable, referenceable, and available to the model as long-form instructions when needed.

## 2. Scope

v1 implements:

1. Automatic discovery of local skills from known roots.
2. Stable skill IDs based on source root and folder name.
3. User enable/disable management UI.
4. Local-only persisted enable-state overrides.
5. System prompt injection of enabled skill index.
6. Diagnostics for skills that can be discovered but not safely loaded.

v1 does not implement:

1. Context-based skill auto-triggering.
2. Skill matching DSL.
3. Embedding/semantic selection.
4. Slash commands.
5. Skill-defined tools.
6. Execution of scripts from skills.
7. Automatic conversion of every `SKILL.md` into a Strategy.

## 3. Relationship Between Skills and Strategies

Skill:

```text
Long-form instruction/resource
Discoverable by registry
Can be enabled or disabled by user
Is exposed to the model with its full SKILL.md file path
Can be read by the model through read_file using that full path
```

Strategy:

```text
Recommended contextual action
Has when/tools/preprocessors/options
Appears in the Strategy UI
One user send executes one Strategy
May later use skill://id as an explicit source reference
```

Strategies may derive from skills, but skills do not automatically become Strategies.

## 4. Skill Identity and Metadata

Skill IDs are stable, case-insensitive, and derived from the source root plus the parent folder name:

```text
everywhere.code-review
agents.sandbox-sdk
codex.imagegen
```

Rules:

1. The ID shape is `<source-root>.<normalized-folder-name>`.
2. Skill IDs do not come from `SKILL.md` frontmatter.
3. Only the official skill metadata fields needed by Skills v1 are parsed, currently `name` and `description`.
4. Strategy Engine fields must not be interpreted as skill metadata.
5. Folder-name collisions are deduped case-insensitively; later duplicates may be ignored.
6. A frontmatter `name` that does not match the folder name is a warning, because folder name remains the ID source.

Invalid or partially invalid skills should remain visible in the management UI with diagnostics when possible.

## 5. Discovery Roots

The managed Everywhere root is:

```text
~/.everywhere/skills
```

It is created by Everywhere when needed. Public/conventional roots are indexed when present:

```text
~/.agents/skills
~/.claude/skills
~/.codex/skills
~/.copilot/skills
~/.cursor/skills
~/.gemini/skills
$CODEX_HOME/skills
```

Only `root/<skill-folder>/SKILL.md` is considered a skill entry. Skills v1 does not recursively scan arbitrary resource folders for additional skill files.

Default enablement is source-level:

1. `Everywhere` and `Agents` skills are enabled by default.
2. Other discovered public roots are loaded but disabled by default.
3. Invalid skills are never injected into the prompt.

## 6. Source Watching

`SkillSource` owns source discovery and file watching. `SkillManager` subscribes to source changes and refreshes its model; it does not manage watcher lifecycle or feed watched paths back into the source.

Current watcher policy:

1. Watch source roots recursively with `FileSystemWatcher.IncludeSubdirectories`.
2. Debounce and aggregate changes before notifying subscribers.
3. Treat only `root/<skill-folder>` and `root/<skill-folder>/SKILL.md` as relevant changes.
4. Ignore ordinary root files and resource files under `references`, `assets`, `scripts`, or similar skill subfolders.
5. On watcher errors or buffer overflow, log a warning and trigger a refresh-oriented change notification.

This keeps the implementation simple for the expected small skill roots while preserving a narrow event contract.

## 7. User Management State

Skill enablement is local-machine state and belongs in `PersistentState`, not synced settings.

Persist only user overrides from the source default:

```json
{
  "SkillEnabledOverrides": {
    "codex.imagegen": true,
    "everywhere.local-style": false
  }
}
```

Rules:

1. Missing entry means use the source default.
2. `true` means user explicitly enabled the skill.
3. `false` means user explicitly disabled the skill.
4. Overrides matching the current default are removed.
5. Overrides for missing skills are cleaned during refresh.

The enable/disable toggle only affects Everywhere prompt assembly; it does not modify the skill file or the external tool that owns the source root.

## 8. System Prompt Injection

Enabled, valid skills are injected as an XML-safe index, not full content:

```xml
<skills>
Here is a list of skills that contain domain specific knowledge on a variety of topics.
Each skill comes with a description of the topic and a file path that contains the detailed instructions.
When a user asks you to perform a task that falls within the domain of a skill, use the 'read_file' tool to acquire the full instructions from the file URI.
<skill>
<name>code-review</name>
<description>Review code when the user asks for feedback.</description>
<file>C:\Users\me\.everywhere\skills\code-review\SKILL.md</file>
</skill>
</skills>
```

Rules:

1. Disabled skills are omitted.
2. Invalid skills are omitted.
3. Full `SKILL.md` content is not injected by default.
4. The prompt includes absolute local paths.
5. The model-facing prompt does not rely on `skill://id`.

The model-facing contract is that enabled skills are listed with complete `SKILL.md` file paths. The model can then call `read_file` with that path when the task falls within the skill's domain.

## 9. `skill://id`

`skill://id` remains an internal/reference URI for Strategy `from`, not a file-reading URI required by Skills v1.

Recommended future policy for Strategy integration:

1. `from: skill://id` resolves installed skills regardless of enabled state, because the Strategy reference is explicit.
2. UI should show that the Strategy references a disabled skill when applicable.
3. `read_file` does not need to support `skill://id` for Skills v1.

## 10. Security and Trust Boundary

Skills are plain text instruction resources.

Rules:

1. Do not execute scripts or commands from skills.
2. Do not automatically grant tools because a skill asks for them.
3. Do not auto-load full skill content into every prompt.
4. Treat skill content as user-managed instruction text.
5. Surface load failures as diagnostics rather than crashing the app.

## 11. Acceptance Criteria

Skills v1 is complete when:

1. `SKILL.md` files are discovered from configured roots.
2. Each discovered skill has stable ID/name/description/path/source information.
3. User can enable and disable skills from SkillPage.
4. Enable-state overrides persist across app restart without syncing across machines.
5. Enabled valid skills are injected into the system prompt using the XML-safe index.
6. Full skill body is not injected by default.
7. Skill source changes trigger automatic refresh for watched roots.
8. Disabled skills do not appear in prompt index.
9. Invalid skill files do not crash the app and appear with diagnostics.
10. No skill content is executed as code.
