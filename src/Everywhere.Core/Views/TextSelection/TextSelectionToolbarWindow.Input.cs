using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.Views;

public partial class TextSelectionToolbarWindow
{
    private bool IsInputComposing => _inputCapture?.IsComposing ?? !string.IsNullOrEmpty(_inputEditor?.PreeditText);

    private IOverlayInputWatch? _inputWatch;
    private IWindowInputCapture? _inputCapture;
    private ChatTextEditor? _inputEditor;
    private bool _isInputPrepared;
    private bool _isUsingActivatedInput;
    private int _generation;

    private void StartWatchingInput()
    {
        if (!_inputWatcher.IsSupported)
        {
            return;
        }

        try
        {
            var generation = _generation;
            _inputWatch = _inputWatcher.Watch(
                _toolbarBounds,
                () => Dispatcher.UIThread.Post(() =>
                {
                    // The callback may already be queued when a watch is disposed. Never let input
                    // from an earlier appearance dismiss a new selection in the reused window.
                    if (generation == _generation)
                    {
                        Hide();
                    }
                }),
                HandlePointerMoved);
            UpdateInputWatch();
        }
        catch (Exception exception)
        {
            StopWatchingInput();
            Log.Warning(exception, "Global toolbar input observation is unavailable; explicit close remains available");
        }
    }

    private void StopWatchingInput()
    {
        _generation++;
        Volatile.Write(ref _updateOpacityFactor, null);
        DisposeHelper.DisposeToDefault(ref _inputWatch);
    }

    private void UpdateInputWatch()
    {
        // Geometry and the input lease have one owner. Every update retains the current lease;
        // resizing the editor or opening a popup must not restore passive keyboard-dismissal rules.
        _inputWatch?.Update(_toolbarBounds, _menuBounds is { } menuBounds ? [menuBounds] : null, _inputCapture);
    }

    private void HandlePointerMoved(PixelPoint position)
    {
        Volatile.Read(ref _updateOpacityFactor)?.Invoke(position);
    }

    /// <summary>
    /// Starts editing after the UI has lazily attached its editor in response to a user interaction.
    /// Prefers delegated input, falling back to ordinary window input when that route is unavailable.
    /// </summary>
    /// <remarks>
    /// Call after switching the ConditionalContentControl to its editor template. This method settles
    /// layout before capture, then focuses ChatTextEditor's inner TextArea, not its outer control.
    /// Do not call from ShowFor or auto-focus the editor before this method. Conversation ownership
    /// and the captured attachment are independent of which input route the editor uses.
    /// </remarks>
    private void HandleFreeformChatInputRequested(ChatTextEditor editor)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_inputCapture is not null)
        {
            if (!_inputCapture.IsActive || !ReferenceEquals(_inputEditor, editor))
            {
                return;
            }

            // Closing an assistant menu restores logical editor focus without requesting another
            // native lease. Menu interaction must not create a second CoreText/capture lifetime.
            editor.Focus();
            return;
        }

        if (!Settings.TextSelection.IsFreeformChatEnabled ||
            !IsVisible || GetTopLevel(editor) != this)
        {
            return;
        }

        if (_isUsingActivatedInput && ReferenceEquals(_inputEditor, editor))
        {
            // Returning from an assistant popup only restores logical focus. Do not retry the
            // unavailable native route or repeatedly activate/log this appearance.
            editor.Focus();
            return;
        }

        ToolbarRoot.CloseMenu();
        editor.ApplyTemplate();
        UpdateLayout();

        if (_isUsingActivatedInput)
        {
            BeginActivatedTextInput(editor);
            return;
        }

        if (!_isInputPrepared)
        {
            BeginActivatedTextInput(editor, "Preparation");
            return;
        }

        var capture = _windowHelper.TryCaptureInput(this);
        if (capture is not null)
        {
            _inputCapture = capture;
            _inputEditor = editor;
            capture.Lost += HandleInputCaptureLost;
#if MACOS
            // AppKit owns the panel's input route, including out-of-process IME candidates.
            // Native key loss dismisses outside interactions without guessing candidate bounds.
            StopWatchingInput();
            OpacityFactor = 1d;
#else
            UpdateInputWatch();
#endif

            // The native input route is already acquired; this sets Avalonia logical focus.
            // IsFocused on ChatTextEditor itself is not meaningful: it delegates to TextArea.
            try
            {
                editor.Focus();
                Log.Debug(
                    "Toolbar editor focused after input capture. Capture active {IsCaptureActive}, editor focused {IsEditorFocused}",
                    capture.IsActive, editor.IsKeyboardFocusWithin);
            }
            finally
            {
                if (!editor.IsKeyboardFocusWithin)
                {
                    EndTextInput();
                }
            }
        }

        if (_inputCapture is null)
        {
            BeginActivatedTextInput(editor, capture is null ? "Capture" : "EditorFocus");
        }
    }

    private void BeginActivatedTextInput(ChatTextEditor editor, string? failureStage = null)
    {
        // Without a delegated lease the global watcher treats typing and IME candidate clicks as
        // outside input. Pause it for ordinary editing; explicit close and local Escape remain.
        StopWatchingInput();
        OpacityFactor = 1d;
        _isUsingActivatedInput = true;
        _inputEditor = editor;

        // Clear logical focus before replacing the input method, so Avalonia resolves the default
        // platform IME again instead of retaining the delegated adapter for the same editor.
        FocusManager.Focus(null);
        _windowHelper.SetWindowProperties(this, inputMode: WindowInputMode.Normal);
        var isForeground = _windowHelper.BringToForeground(this);
        editor.Focus();

        if (failureStage is not null)
        {
            Log.Warning(
                "Non-activating toolbar input unavailable at {FailureStage}; using ordinary window input. Foreground {IsForeground}, editor focused {IsEditorFocused}",
                failureStage, isForeground, editor.IsKeyboardFocusWithin);
        }
    }

    /// <summary>
    /// Releases editing input before the UI removes its editor or returns to toolbar mode.
    /// Does not activate or restore focus to another native window.
    /// </summary>
    private void EndTextInput()
    {
        Dispatcher.UIThread.VerifyAccess();
        var capture = _inputCapture;
        var editor = _inputEditor;
        _inputCapture = null;
        _inputEditor = null;
        if (capture is not null)
        {
            capture.Lost -= HandleInputCaptureLost;

            // Clear the hook's borrowed reference before releasing native input. Stop also clears
            // preedit without committing an unfinished composition. The source was never deactivated,
            // so neither SetForegroundWindow nor synthetic modifier releases belong in this teardown.
            UpdateInputWatch();
            capture.Dispose();
        }

        if (editor?.IsKeyboardFocusWithin == true)
        {
            FocusManager.Focus(null);
        }
    }

    private void HandleFreeformChatExiting()
    {
        var hasEditor = _inputEditor is not null;
        EndTextInput();
        if (_isUsingActivatedInput && hasEditor && IsVisible)
        {
            _windowHelper.SetWindowProperties(this, inputMode: WindowInputMode.NonActivating);
            StartWatchingInput();
            UpdateOpacityTracking();
        }
#if MACOS
        else if (hasEditor && IsVisible)
        {
            StartWatchingInput();
            UpdateOpacityTracking();
        }
#endif
    }

    private void HandleInputCaptureLost(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _inputCapture))
        {
            return;
        }

        Hide();
    }
}