# Prompt Page Design

## 1. Goal

The Prompt Manager page is the primary surface for browsing, previewing, editing, copying, deleting, and inspecting prompt references.

The page should feel like a sibling of the custom assistant page and the skill page. It should reuse their visual vocabulary instead of introducing a separate management style.

## 2. Navigation

The page is a standalone main navigation item placed after the custom assistant page.

Opening the page does not select a prompt automatically. The content area shows an empty state with the same image-and-message style used by the custom assistant page. This avoids surprising users by immediately focusing or editing the built-in default prompt.

Main navigation supports browser-style string routes. Route constants and builders live on `MainViewNavigateMessage`; page links should use those helpers instead of hard-coded path strings. The first path segments match main navigation route keys and decoded remaining segments are delivered to the target view model through `OnNavigatedTo`.

Prompt Manager should later expose the route shape `PromptPage/{promptGuid}` through `MainViewNavigateMessage.ToPrompt(prompt.Id)`.

## 3. Left Pane

The left pane lists all prompts.

Requirements:

1. title and primary create action at the top
2. search box below the header
3. prompt list below search
4. selected item synchronized with the right content
5. default prompt pinned at the top
6. custom prompts sorted predictably, preferably by user order first and name second if no explicit order exists

The left pane should reference the custom assistant page for item density, icon/name presentation, and create button placement. It should reference the skill page for search behavior and empty/filter states.

Prompt names are optional. List item title resolution should use the prompt display-name fallback from `02-DataModel.md`.

## 4. Default Prompt Item

The built-in default prompt is visible in the list and uses `Guid.Empty`.

Behavior:

1. pinned above custom prompts
2. marked with a built-in/default badge
3. can be selected
4. can be copied
5. can show references
6. cannot be edited
7. cannot be deleted
8. is not stored in `prompt.db`

## 5. Selected Prompt Read Mode

The selected prompt content starts in read mode.

Structure:

```text
top diagnostics
header actions
content body
  left: rendered preview / raw template tabs
  right: assistant references
```

Top diagnostics are page-level toasts and appear above the main content, not inside preview cards. They are not dismissible and are recalculated when prompt state changes.

The preview tab shows rendered content. The template tab shows raw template text in read-only mode. Placeholders remain highlighted in raw template text even when read-only.

## 6. Actions

Header actions should match the custom assistant page style where possible.

Actions:

1. Copy
2. Edit
3. Delete

Copy copies rendered content, not raw template text.

Edit switches to edit mode. It is disabled or hidden for the built-in default prompt.

Delete is blocked for the built-in default prompt. Deleting a referenced custom prompt requires strong confirmation. After confirmation, assistant references to the deleted prompt are reset to `Guid.Empty`.

## 7. Assistant References

The right references panel shows assistants that reference the selected prompt.

First version scope:

1. assistant references only
2. show count in the panel title
3. show assistant name and model summary
4. clicking an assistant navigates to the custom assistant page and selects that assistant

Reference clicks should use the main string route helpers instead of global selection messages:

```csharp
WeakReferenceMessenger.Default.Send(
    new MainViewNavigateMessage(MainViewNavigateMessage.ToCustomAssistant(assistant.Id)));
```

The route shape is `CustomAssistantPage/{assistantGuid}`. The main navigation layer matches `CustomAssistantPage` and passes the remaining GUID segment to `CustomAssistantPageViewModel.OnNavigatedTo`.

## 8. Responsive Behavior

Only the selected prompt content body changes layout at narrow widths.

The main page still uses the NavigationView left pane and selected content structure. The right content body changes from:

```text
preview/template | references
```

to:

```text
preview/template
references
```

Use the existing `AdaptiveBehavior` pattern from HomePage to apply `narrow` and `wide` classes. The breakpoint should be chosen based on content readability, not on the whole application window alone.

## 9. Diagnostics

Diagnostics shown on this page:

1. missing `{DefaultSystemPrompt}`
2. missing `{SkillsPrompt}` only when `{DefaultSystemPrompt}` is absent
3. unknown placeholders
4. preview context limitations
5. referenced prompt delete warning

Missing `{DefaultSystemPrompt}` and missing `{SkillsPrompt}` are reminders only. They do not block saving.

## 10. Open Design Topics

Advanced edit mode is specified in `04-EditorPreviewDiagnostics.md`.
