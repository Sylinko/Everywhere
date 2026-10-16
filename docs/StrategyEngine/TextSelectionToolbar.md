# Text selection toolbar integration

## Independent consumers

`TextSelection.IsAutomaticAttachmentEnabled` is the automatic **chat attachment** preference. `TextSelection.IsToolbarEnabled` is the toolbar preference. Both preferences and the toolbar tools live in `TextSelectionSettings`. The page groups toolbar settings first, chat attachment settings second, and advanced application rules last. No migration from the unmerged PR toolbar subtree is provided.

`TextSelectionController` owns the shared settings command. It requests the OR of the supported consumers from `ITextSelectionWatcher` and commits the changed preference after acknowledgment. Both switches observe the same running operation. Turning off the final consumer also commits the local disabled state if the Host cannot acknowledge shutdown, matching the watcher's delivery contract. A definitive restoration rejection resets supported consumers and is reported through the existing startup error notification.

| Automatic attachment | Toolbar supported and enabled | Native monitoring |
| --- | --- | --- |
| Off | Off | Off |
| On | Off | On |
| Off | On | On |
| On | On | On |

## Selection ownership

`ChatWindowViewModel` remains the only observer. An observation's source Anchor is moved once into a `TextSelectionAttachment`.

- If automatic capture accepts it, the chat draft owns the attachment and `TextSelectionToolbarService` only borrows it. Draft removal or submission hides the borrowing toolbar.
- Otherwise, the toolbar owns the attachment until dismissal or action invocation. Unsupported, unmatched, or superseded selections are disposed.
- Invoking an action moves ownership to the chat draft before draft change notifications run. The existing send pipeline moves its Anchor into the destination chat Context.
- The toolbar checks the original observation's revision before acting. A queued dismissal callback is associated with one presentation generation and cannot hide a later selection.

The gesture position is captured by the native detector and travels with the observation as scalar RPC coordinates. Main no longer queries the removed visual-element-context API for pointer position.

## Shared presentation

`TextSelectionToolbar` renders the floating toolbar and settings preview. `VisibleActionCount` limits the directly visible tools, not strategy matching, and may be zero. Remaining tools appear in a bottom-right-aligned menu, followed by an application exclusion command. Each tool independently controls its button label; menu items always retain their names. Both hosts use the same SmoothBorder, resources, icons, and templates.

The close button dismisses only the current toolbar. Its protruding bounds remain inside the native window. The dismissal watcher treats the menu as a separate internal rectangle and allows its scrolling and navigation keys. Menu lifetime ends before the toolbar window is hidden or reused.

Platform `ExcludedApplications` rules suppress all automatic selection capture. `ToolbarExcludedApplications` suppress only the toolbar, preserving automatic chat attachment. The lists are independent and use the same application editor. The native detector captures the executable path (Windows) or bundle identifier (macOS) with each observation, independently of source-anchor availability, and sends it over RPC. The menu excludes that captured application rather than whichever window happens to be foreground when clicked.

The preview filters enabled, available configured actions incrementally and binds those same action objects directly. It deliberately does not execute matching, construct execution strategies, send messages, or change the selected assistant. The live toolbar applies matching before presentation and retains its matched strategy snapshots in the window for execution. Both presentations bind the action's display properties, so editing names, icons, or label visibility does not replace buttons. The editor also binds the persisted action collection directly so the existing `Draggable` behavior saves ordering.

## Polymorphic action configuration

`TextSelectionToolbarAction` contains shared identity and display preferences. `BuiltInTextSelectionToolbarAction` references a provider-qualified `StrategyId` and stores optional name, icon, and prompt overrides; null means inherit the registered default. `CustomTextSelectionToolbarAction` stores its own validated name, icon, and prompt. The ordered collection may contain both types, persisted with the existing SettingsEngine polymorphic serialized-subtree support. No migration from the unmerged PR's flat action format is provided.

`TextSelectionToolbarActionsManager` resolves runtime-only registered metadata when entries are added or replaced, including settings reload/import, and unsubscribes when disposed. Unregistered built-in references remain editable in settings but are unavailable in the preview and execution. Each concrete action constructs its own execution strategy; built-in overrides clone the matched strategy to preserve conditions, preprocessors, system prompts, and tool permissions.

The editor uses typed templates for the type-specific fields and reset/delete operations. Dialogs edit same-type copies with independent icons, then apply accepted changes to the original action. An inherited localized name is captured for the duration of the draft so accepting an unchanged name does not persist a translation as an override, even if the language changes while the dialog is open.

`ChatCustomAssistantSelector` accepts `Classes="Compact"`. Its existing selector remains interactive; the button hides name and reasoning labels while its tooltip shows their current values.

## Adaptive opacity and native input

`IsToolbarOpacityAdaptive` optionally multiplies `ToolbarOpacity` by a pointer-distance factor. The factor is 1 within 24 DIP of the toolbar or its menu, falls through a smooth interpolation, and reaches 0.2 at 300 DIP. It is quantized to 0.05 before multiplication, retaining the user's base opacity and avoiding rounding a low final opacity to zero. Both the live toolbar and the settings preview use this policy with a short opacity transition.

`IOverlayInputWatcher` combines native dismissal input and pointer-position callbacks. Windows reuses `LowLevelHook`; macOS reuses `CGEventListener` through a scoped listen-only session with a dedicated run-loop thread. The session's tap, source and thread are released after the final watch ends. Existing process-lifetime CGEvent listeners are initialized lazily, so creating an overlay session does not initialize unrelated taps. Copy-fallback keystrokes carry a marker across Automation Host and Main and do not dismiss their own toolbar.

The UI thread publishes a calculation delegate capturing current bounds and desktop scaling when geometry or settings change, and immediately recalculates the initial factor. Native pointer callbacks calculate the factor and compare integer steps before dispatching. Each delegate coalesces changed steps into one pending UI operation, which multiplies its latest factor by the user's base opacity. Replacing or clearing the delegate makes its old queued callbacks obsolete, including when hiding the toolbar or disabling adaptive opacity. There is no pointer polling or fixed-frequency opacity timer. The preview uses local window/menu pointer events and installs no global listener.

Screen coordinates follow Avalonia's platform desktop space: physical pixels on Windows and top-left-origin desktop points on macOS. Distances are converted with `DesktopScaling`, while menu bounds are measured by transforming both corners with `PointToScreen`; Retina `RenderScaling` must not be applied to macOS desktop positions. This matches [Avalonia's native screen-coordinate conversion](https://github.com/AvaloniaUI/Avalonia/blob/master/native/Avalonia.Native/src/OSX/TopLevelImpl.mm).

## Remaining platform and chat work

The live toolbar supports Windows and macOS global input observation. Overlay input hooks still run in Main; moving them into Input Host should accompany the platform focus work. Automatic text-selection detection itself runs through Automation Host. Linux retains an explicitly unsupported overlay-input implementation.

The optional chat button, inline composer, temporary-chat ownership and persistence, small chat window, and platform input/focus policy remain separate stages. No temporary-chat lifecycle is implied by this implementation.
