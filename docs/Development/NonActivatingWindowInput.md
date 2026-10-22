# Non-activating window input

Show the passive toolbar first; create and focus its editor only after the user explicitly switches to editing. Prefer delegated input, which keeps the source application foreground. If preparation or capture is unavailable, the toolbar explicitly falls back to ordinary activated input. All preparation, capture, focus and disposal operations belong to the UI thread.

## Toolbar UI integration

`TextSelectionToolbarService` owns the selected attachment (or borrows it from the chat draft), selection validity, strategy matching/execution and application exclusion. `TextSelectionToolbarWindow` owns presentation, native input capture and its global input watch. The service never accesses the inner toolbar, popup geometry or capture lease.

The window binds its settings source in AXAML: action count, available assistants, the chat-entry preference and the product of configured opacity and pointer-distance factor. Each appearance initializes a local assistant choice from the main chat; subsequent choices do not write back to the main chat's settings. Only behavior-changing settings need window callbacks: adaptive-opacity changes rebuild copied hook geometry, and disabling freeform chat releases editing input. Toolbar disable/exclusion settings remain service-owned session-validity rules.

1. `ShowFor` resets the pooled presentation, configures `NonActivatingTextInput` and calls `IWindowHelper.TryPrepareInput`. The passive branch has no editor in its visual tree. The freeform-chat preference controls the live toolbar's chat entry independently of preparation success; a chat-only toolbar can appear even when no strategy actions match. If neither tools nor enabled freeform input are available, `ShowFor` returns false without showing an empty toolbar.
2. The chat button changes `IsFreeformChat`. `ConditionalContentControl` lazily builds the `ChatInputArea` branch during layout. After normal layout the control raises `FreeformChatInputRequested` with its inner `ChatTextEditor`; the window captures the prepared native route and logically focuses the editor. Preparation, capture or initial logical-focus failure selects ordinary activated input instead of returning to tools. There is no automatic focus before capture or timing delay.
3. The back button raises `FreeformChatExiting` before changing branches, so the window releases input before the outgoing editor animates away. `ResetFreeformChat` commits the passive branch without the old editor's page transition when hiding/reusing the window; cached branches remain available for later explicit switches.
4. Hiding, minimizing or closing ends capture. The window releases capture before notifying the service of an action, so strategy execution can activate the chat window safely. Explicit release clears unfinished preedit and logical editor focus; it does not restore native foreground or synthesize key releases. Hiding resets menus and presentation before notifying the service to release its selection; the notification does not call back into window cleanup.
5. The assistant flyout is observed only while its control is attached. It shares popup bounds/opacity tracking with the tools menu and restores logical editor focus on close without requesting another lease. Toolbar-scoped popup policy prevents native focus transfer. Windows native hit testing also recognizes same-thread popup owner chains rooted at this toolbar, covering interactive child popups without borrowing UI objects on the hook thread.

Freeform sends an ordinary `UserChatMessage` containing the typed text and this selection's attachment to the main chat window. The toolbar's assistant is passed explicitly to `IChatService.SendMessage`; it does not overwrite the global assistant. `ChatWindowViewModel.SendTextSelectionMessage` follows the existing new-chat preference without using the main draft's send command, cancelling edits or consuming unrelated draft text and attachments. Independent sessions and database changes are deferred.

Submission is synchronous up to acceptance. The window releases native input before invoking its business owner. A busy destination rejects without consuming the message; the editor retains its text and reacquires the same prepared input route. Acceptance transfers attachment ownership to the chat service, removes an exact borrowed attachment from the main draft, clears the toolbar and shows the main window. Visual processing moves acquisition-context anchors into the destination context. The accepted user message is added before visual-turn preparation so a preparation failure cannot erase its text.

Ordinary-input fallback clears logical focus, selects `WindowInputMode.Normal` to restore the platform IME, activates the toolbar and focuses its editor. It retains that choice for the current appearance, including assistant-popup focus restoration and rejected submissions, without retrying delegation. A warning records the failure stage and activation/focus outcome, never the selected or typed text. Returning to tools restores non-activating window policy; hiding resets the choice for the next selection. The attachment's captured text and retained source remain owned by the service throughout. Loss of an already-established delegated capture still hides the toolbar rather than stealing foreground back.

Returning to tools retains the draft for the same selection. Hiding or reusing the pooled window clears it, including the cached editor branch. `ChatInputArea.ShouldClearTextOnSubmit` is false here because the host clears only accepted submissions; existing composers retain their immediate-clear behavior. The settings preview has neither a send command nor a capture handler.

Esc uses the existing delegated Avalonia key route: active composition belongs to the IME, an open menu closes first, otherwise editing returns to tools. Enter uses the ordinary `ChatInputArea` send behavior. Ctrl+Shift+Space is deferred: passive windows have no local keyboard focus and the separate global shortcut and dismissal pipelines would need coordination. No shortcut is advertised for entering freeform mode.

## Platform API and observation

`IWindowHelper.TryPrepareInput(window)` prepares the platform route without acquiring input. On Windows it initializes the native host without creating a CoreText context; on macOS it validates the non-activating panel. It records the foreground source. `TryCaptureInput(window)` requires the prepared route, a visible non-minimized window and the same source. Both are synchronous UI-thread methods. Preparation returns false and capture returns null when unavailable; neither activates the application as a fallback. One native capture may exist at a time.

These are intentionally separate operations. First-use probes that created the host and requested capture in the same interaction sometimes returned success and focused the hidden host without delegating real input. An added dispatcher yield did not resolve that behavior. The accepted route prepares while the passive toolbar is appearing and captures on the later explicit editing interaction. There is no fixed delay, message pump, polling or automatic retry.

`WindowInputMode.Normal` retains ordinary activation. `NonActivating` is for passive overlays and pickers. `NonActivatingTextInput` allows logical focus and, on Windows, installs the platform input adapter. macOS uses the existing Avalonia native view and input-method implementation. This mode does not itself prepare or capture. Configure it before logically focusing an editor.

The window supplies its current lease on **every** overlay-watch geometry update and clears it synchronously before release. Keyboard input and scrolling inside the editor no longer dismiss an editing toolbar; outside pointer interaction still does. The watcher observes through the existing Windows LLHook / macOS CGEvent pipelines and never suppresses input. Hook callbacks consume copied geometry, not Avalonia objects. Interactive menus are included; passive preedit/tooltip windows are not. Pointer distance is quantized in 0.05 steps on the hook thread before coalescing UI updates. Geometry revisions reject obsolete opacity callbacks, and appearance generations reject obsolete dismissal callbacks after hide/reuse.

Global observation is optional: an unsupported watcher or failed watch startup does not disable monitoring, settings or toolbar display. Explicit close remains available and opacity uses the configured value without distance scaling. Ordinary activated editing pauses the global watch so typing and IME candidate clicks cannot dismiss it; returning to tools resumes the watch. This fallback deliberately relies on explicit close and local Escape rather than another native popup-detection mechanism.

## Windows native route

Three focus roles remain distinct:

- The original application keeps the foreground window.
- A hidden desktop CoreWindow owns delegated native input.
- The visible editor owns Avalonia logical focus.

`CoreWindowInputHost` initializes the UI STA with `RoInitialize(RO_INIT_SINGLETHREADED)`. It creates a desktop CoreWindow using Windows.UI.dll ordinal 1500 with type 6 and retrieves its HWND via `ICoreWindowInterop` (IID `45D64A29-A63E-4CB6-B498-5781D298CB4F`, slot 3). The hidden HWND is non-activating and does not appear in the taskbar.

The CoreWindow exposes `ITextInputConsumer` (IID `a9d00ab3-2fef-41a0-b0ad-4b2129ea2663`). Windows.UI.Core.TextInput.dll ordinal 1500 creates its producer, and consumer slot 7 connects it. Both the CoreWindow and producer remain alive for the helper's UI-thread lifetime, across individual toolbar appearances.

`WindowsUdk.UI.Input.Text.TextInputCapture` exposes Current through statics IID `efb97a91-0252-56e1-96f7-7d444006f569`, slot 6. The returned current-thread object has RequestCapture/ReleaseCapture at slots 6/7. `WindowsWindowInputCapture` owns one request/release pair and the subscriptions for lifetime, layout and key forwarding. The CoreText context stops before native capture releases. The helper releases its shared host on the UI dispatcher's ShutdownStarted event; later service disposal has no native host left to release on a worker thread.

`Everywhere.Windows.Interop/TextInput` contains the ordinal imports and private, non-owning COM ABI structs. They reuse CsWin32's `IUnknown`, generic interface queries, SDK-defined `ICoreWindowInterop`, `IInspectable` vtable prefix and `HRESULT`. `DesktopTextInputInterop` exposes only the narrow operations needed by the host, without leaking assembly-local generated types. `CoreWindowInputHost` retains ownership of returned references and releases capture, closes the CoreWindow, releases producer/window references and uninitializes WinRT in the original UI-thread order. The ABI layer has no finalizer or automatic input acquisition.

These ordinals and private COM layouts were investigated on Windows 11 build 26100.9711. The user also confirmed the input route works in a Windows 10 build 19041 virtual machine. These are runtime observations, independent of the 19041 compilation target; other builds remain unverified. Missing exports or interfaces fail through the preparation/capture boundary; the toolbar then chooses ordinary activated editing on the user's explicit chat-entry interaction.

The MonoMod Win32 patch exposes `IWindowInputFeature`, disables `WindowImpl.ShouldTakeFocusOnClick` and replaces the platform `ITextInputMethodImpl`. Existing native styles and `WM_MOUSEACTIVATE` handling stay in place. Normal activation/focus messages continue through Avalonia; no DispatchProxy, CBT hook or Activate interception is required.

`WindowsTextInputMethod` adapts CoreText to the editor's ordinary `TextInputMethodClient`. Intermediate composition uses `SetPreeditText` and the existing AvaloniaEdit preedit presentation; committed text enters Avalonia's raw text-input pipeline once. Navigation and shortcuts use raw key events. CoreText may mark navigation handled before Avalonia has seen it, so that native flag alone must not discard the event. CharacterReceived and WM_CHAR are not a second text-delivery channel.

Avalonia supplies the caret rectangle in top-level logical coordinates. CoreText receives the actual physical screen caret and text-view bounds. Movement and scaling notify layout changes; an extra editor-offset transform would be incorrect.

## macOS native route

`TextSelectionToolbarWindow` is created with `AvaloniaNativeWindowFactory.CreatePanel` and `IsNonActivating = true`. The native object must be an `NSPanel` with the `NonactivatingPanel` style from creation.

`WindowHelper.TryPrepareInput` validates that panel and records the frontmost application's process ID. The later capture checks the same source, the visible non-minimized panel and its existing Avalonia `NSView`. `MacWindowInputCapture.Start` makes the panel key with `MakeKeyAndOrderFront` and sets that view as first responder. It never activates `NSApplication`; the source application remains frontmost while the panel receives keyboard input.

AppKit and Avalonia's existing native input implementation deliver committed text, marked text and candidate-window layout to `ChatTextEditor`. There is no hidden CoreWindow, private Windows-style input producer or second text-delivery path. `IsComposing` queries the view's `hasMarkedText` state so local Escape handling can leave active composition to the IME.

The capture observes `DidResignKeyNotification`, hiding, minimization and close. Involuntary loss disposes the lease and notifies the toolbar. Explicit release removes observers, resets the existing Avalonia input method to clear marked text/preedit and resigns panel key status before clearing helper ownership. All of this runs on the UI thread; release does not activate or restore the source application. The existing CGEvent pipeline continues to observe overlay dismissal and pointer distance rather than forwarding text input.

## Evidence and remaining boundaries

The accepted independent `.build/input-toolbar-chat-editor` probe uses the actual woven Win32 backend, production host/capture/adapter algorithm, and actual `ChatTextEditor` with patched AvaloniaEdit and its preedit theme. On 2026-10-20 the user confirmed Chinese composition/candidates, clicking and continuing input, multiline/navigation/selected-text replacement, and direct source typing after mouse close. The editor count is zero before switching and one after; release logs show inactive capture and cleared host ownership.

Before the window-ownership refactor, the temporary `.build/input-toolbar-api-smoke` invoked the built product `WindowHelper` and `TextSelectionToolbarWindow`, with the real editor and woven backend. A separate probe-owned source process stayed foreground. Passive preparation, lazy editor capture/focus, duplicate rejection, explicit release, editor reacquisition, policy-change loss notification, preparation invalidation, hide/reuse and changed-source rejection passed. It injected no input. Logs: `.build/input-toolbar-api-smoke.log`; Windows build: `.build/input-product-integration-build.log` (zero warnings/errors). This records the native route's earlier evidence; the removed public capture hooks are no longer the window's production interface.

The actual toolbar UI is now connected. `.build/input-toolbar-ui-smoke` uses its real buttons, lazy ChatInputArea, page transitions and assistant flyout, rather than replacing the presentation. It passed switch/back capture, source foreground preservation, popup ownership hit testing, native-focus preservation, logical-focus restoration, editor detachment, recapture and immediate hide/reuse. `.build/input-toolbar-ui-render` uses the normal headless compositor for actual control renders; ImmediateRenderer cannot render the current SmoothBorder stack. Logs: `.build/input-toolbar-ui-smoke.log` and `.build/input-toolbar-ui-render.log`; Windows build: `.build/input-ui-integration-build.log`.

The shared ChatInputArea check also preserves cancellation command states and normal text/attachment-only send eligibility; null text/collections no longer leave the Boolean binding unset.

The user subsequently verified the integrated toolbar's Chinese input, reopening without an editor-frame flash, and closing/reopening during animation. Cold startup without opening the main window also passed after adding SWP_NOACTIVATE to the hidden branch of SetCloaked; the visible branch retains its existing activation behavior. The later best-effort activation fallback preserved Chinese input in another cold-start check. Automatic UI checks do not inject keyboard input.

Known boundaries:

- Moqi candidate placement outside the source application's bounds also fails with Win+.; report real coordinates rather than compensating for that IME behavior.
- Native popup hit testing accepts this toolbar's same-thread popup owner chains and IME-class windows owned by the capture thread. Candidate UI in a separate process needs ownership evidence before exemption from outside-click dismissal.
- Releasing through Ctrl+Alt+F10 while modifiers are held could leave Notepad in Alt menu mode; Esc restores typing. Mouse-close passed. Do not infer physically stuck modifiers or synthesize corrective keys from that observation.
- Upstream AvaloniaEdit current-line text and selection offsets warrant separate investigation if a failure appears. The accepted editing scenarios did not reproduce an offset bug; no speculative editor-client rewrite is included. Leading attachment content and involuntary-loss/reentry need checking with their actual UI flows.
- macOS requires the non-activating `NSPanel` and its existing native view. If either is unavailable or capture cannot preserve the prepared source, the shared ordinary activated-input fallback remains available.
