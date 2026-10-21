using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Everywhere.Patches.Contracts.Interop;
using Windows.UI.Text.Core;
using EditorSelection = Avalonia.Input.TextInput.TextSelection;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Adapts a delegated CoreText context to the window's normal Avalonia editor client.
/// The editor owns its document; this adapter only projects the active composition to CoreText.
/// </summary>
internal sealed class WindowsTextInputMethod(Window window, IWindowInputFeature feature) : ITextInputMethodImpl
{
    public bool IsComposing { get; private set; }

    public event Action? FocusLost;

    private TextInputMethodClient? _client;
    private CoreTextEditContext? _context;
    private string _imeText = string.Empty;
    private CoreTextRange _selection;
    private EditorSelection _commitRange;
    private Rect _cursorRect;
    private int _compositionStart;
    private int _compositionLength;
    private bool _hasPreedit;
    private bool _isUpdating;
    private bool _hasFocus;
    private bool _isEnding;

    public void Start()
    {
        Dispatcher.UIThread.VerifyAccess();
        // GetForCurrentView resolves the UI thread's prepared CoreWindow, not the visible HWND.
        // No context exists in passive toolbar mode. FocusEnter waits for the normal Avalonia
        // editor client, supplied when the caller logically focuses ChatTextEditor after capture.
        var context = CoreTextServicesManager.GetForCurrentView().CreateEditContext();
        _context = context;
        context.InputScope = CoreTextInputScope.Text;
        context.InputPaneDisplayPolicy = CoreTextInputPaneDisplayPolicy.Manual;
        context.TextRequested += HandleTextRequested;
        context.SelectionRequested += HandleSelectionRequested;
        context.LayoutRequested += HandleLayoutRequested;
        context.TextUpdating += HandleTextUpdating;
        context.SelectionUpdating += HandleSelectionUpdating;
        context.FormatUpdating += HandleFormatUpdating;
        context.CompositionStarted += HandleCompositionStarted;
        context.CompositionCompleted += HandleCompositionCompleted;
        context.FocusRemoved += HandleFocusRemoved;
        Enter();
    }

    public void Stop()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_context is not { } context)
        {
            return;
        }

        try
        {
            Leave();
        }
        finally
        {
            context.TextRequested -= HandleTextRequested;
            context.SelectionRequested -= HandleSelectionRequested;
            context.LayoutRequested -= HandleLayoutRequested;
            context.TextUpdating -= HandleTextUpdating;
            context.SelectionUpdating -= HandleSelectionUpdating;
            context.FormatUpdating -= HandleFormatUpdating;
            context.CompositionStarted -= HandleCompositionStarted;
            context.CompositionCompleted -= HandleCompositionCompleted;
            context.FocusRemoved -= HandleFocusRemoved;
            _context = null;
        }
    }

    public void SetClient(TextInputMethodClient? client)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (ReferenceEquals(_client, client))
        {
            return;
        }

        Leave();
        if (_client is { } previous)
        {
            previous.SurroundingTextChanged -= HandleClientChanged;
            previous.SelectionChanged -= HandleClientChanged;
        }

        _client = client;
        if (client is not null)
        {
            client.SurroundingTextChanged += HandleClientChanged;
            client.SelectionChanged += HandleClientChanged;
        }

        Enter();
    }

    public void SetCursorRect(Rect rect)
    {
        // Avalonia already translates the client's caret into top-level logical coordinates.
        // Applying the editor offset again here would displace the IME candidate window.
        _cursorRect = rect;
        LayoutChanged();
    }

    public void LayoutChanged()
    {
        if (_hasFocus)
        {
            _context?.NotifyLayoutChanged();
        }
    }

    public void SetOptions(TextInputOptions options)
    {
        // This input surface is a chat editor, not a password or numeric field.
    }

    public void Reset()
    {
        Leave();
        Enter();
    }

    public bool DispatchKey(Key key, PhysicalKey physicalKey, RawKeyEventType type, RawInputModifiers modifiers)
    {
        if (feature.InputRoot is not { } root)
        {
            return false;
        }

        var keyboard = AvaloniaLocator.Current.GetRequiredService<IKeyboardDevice>();
        var args = new RawKeyEventArgs(keyboard, (ulong)Environment.TickCount64, root, type, key, modifiers, physicalKey, null);
        window.PlatformImpl?.Input?.Invoke(args);
        return args.Handled;
    }

    private void Commit(string text)
    {
        if (feature.InputRoot is not { } root)
        {
            return;
        }

        var keyboard = AvaloniaLocator.Current.GetRequiredService<IKeyboardDevice>();
        window.PlatformImpl?.Input?.Invoke(new RawTextInputEventArgs(keyboard, (ulong)Environment.TickCount64, root, text));
    }

    private void Enter()
    {
        if (_context is not { } context || _client is null || _hasFocus)
        {
            return;
        }

        SynchronizeClient(false);
        _hasFocus = true;
        context.NotifyFocusEnter();
        context.NotifyTextChanged(default, _imeText.Length, _selection);
        context.NotifyLayoutChanged();
    }

    private void Leave()
    {
        // NotifyFocusLeave can synchronously complete composition. Closing/resetting must not
        // insert the unconfirmed preedit into the editor as a side effect of releasing capture.
        _isEnding = true;
        try
        {
            if (_hasFocus)
            {
                _hasFocus = false;
                _context?.NotifyFocusLeave();
            }
        }
        finally
        {
            _client?.SetPreeditText(null, null);
            IsComposing = false;
            _isEnding = false;
        }
    }

    private void SynchronizeClient(bool shouldNotify)
    {
        var previous = _imeText;
        _imeText = _client?.SurroundingText ?? string.Empty;
        var selection = _client?.Selection ?? default;
        _selection = new CoreTextRange
        {
            StartCaretPosition = Math.Clamp(Math.Min(selection.Start, selection.End), 0, _imeText.Length),
            EndCaretPosition = Math.Clamp(Math.Max(selection.Start, selection.End), 0, _imeText.Length)
        };

        if (!shouldNotify || !_hasFocus || _context is not { } context)
        {
            return;
        }

        if (string.Equals(previous, _imeText, StringComparison.Ordinal))
        {
            context.NotifySelectionChanged(_selection);
        }
        else
        {
            context.NotifyTextChanged(new CoreTextRange { EndCaretPosition = previous.Length }, _imeText.Length, _selection);
        }
    }

    private void HandleClientChanged(object? sender, EventArgs args)
    {
        if (!_isUpdating && !IsComposing && !_isEnding)
        {
            SynchronizeClient(true);
        }
    }

    private void HandleTextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        var range = args.Request.Range;
        var start = Math.Clamp(range.StartCaretPosition, 0, _imeText.Length);
        var end = Math.Clamp(range.EndCaretPosition, start, _imeText.Length);
        args.Request.Text = _imeText[start..end];
    }

    private void HandleSelectionRequested(CoreTextEditContext sender, CoreTextSelectionRequestedEventArgs args)
    {
        args.Request.Selection = _selection;
    }

    private void HandleLayoutRequested(CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        // CoreText requires physical screen coordinates. Report the actual caret and text-view
        // bounds, and notify again on native movement/scaling through the capture lease.
        // Moqi also mispositions Win+.'s candidate UI outside the original source window bounds;
        // that observed IME limitation is not a reason to falsify these coordinates.
        var point = window.PointToScreen(_cursorRect.TopLeft);
        var scale = window.RenderScaling;
        args.Request.LayoutBounds.TextBounds = new global::Windows.Foundation.Rect(
            point.X, point.Y, Math.Max(1, _cursorRect.Width * scale), Math.Max(1, _cursorRect.Height * scale));

        var visual = _client?.TextViewVisual ?? window;
        var origin = visual.PointToScreen(default);
        args.Request.LayoutBounds.ControlBounds = new global::Windows.Foundation.Rect(
            origin.X, origin.Y, visual.Bounds.Width * scale, visual.Bounds.Height * scale);
    }

    private void HandleTextUpdating(CoreTextEditContext sender, CoreTextTextUpdatingEventArgs args)
    {
        var start = args.Range.StartCaretPosition;
        var end = args.Range.EndCaretPosition;
        if (_client is not { } client || _isEnding || start < 0 || end < start || end > _imeText.Length)
        {
            args.Result = CoreTextTextUpdatingResult.Failed;
            return;
        }

        var incoming = args.Text ?? string.Empty;
        var updatedText = _imeText[..start] + incoming + _imeText[end..];
        var compositionLength = _compositionLength + incoming.Length - (end - start);
        if (IsComposing && (compositionLength < 0 || _compositionStart + compositionLength > updatedText.Length))
        {
            args.Result = CoreTextTextUpdatingResult.Failed;
            return;
        }

        _imeText = updatedText;
        _selection = args.NewSelection;
        _isUpdating = true;
        try
        {
            if (IsComposing)
            {
                _compositionLength = compositionLength;
                var preedit = _imeText.Substring(_compositionStart, _compositionLength);
                // Reuse AvaloniaEdit's existing preedit pipeline, including the patched composition
                // indicator. The document receives committed text once, not each intermediate update.
                client.SetPreeditText(preedit, Math.Clamp(_selection.EndCaretPosition - _compositionStart, 0, preedit.Length));
                if (!_hasPreedit && preedit.Length > 0)
                {
                    // Some Avalonia clients delete the selected document text on first preedit;
                    // others only render an overlay. Preserve the client's resulting selection.
                    _commitRange = client.Selection;
                    _hasPreedit = true;
                }
            }
            else
            {
                client.Selection = new EditorSelection(start, end);
                if (incoming.Length > 0)
                {
                    Commit(incoming);
                }
                else if (end > start)
                {
                    DispatchKey(Key.Delete, PhysicalKey.Delete, RawKeyEventType.KeyDown, RawInputModifiers.None);
                    DispatchKey(Key.Delete, PhysicalKey.Delete, RawKeyEventType.KeyUp, RawInputModifiers.None);
                }

                client.Selection = new EditorSelection(args.NewSelection.StartCaretPosition, args.NewSelection.EndCaretPosition);
                SynchronizeClient(false);
            }

            args.Result = CoreTextTextUpdatingResult.Succeeded;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void HandleSelectionUpdating(CoreTextEditContext sender, CoreTextSelectionUpdatingEventArgs args)
    {
        _selection = args.Selection;
        _isUpdating = true;
        try
        {
            if (IsComposing)
            {
                var preedit = _imeText.Substring(_compositionStart, _compositionLength);
                _client?.SetPreeditText(preedit, Math.Clamp(_selection.EndCaretPosition - _compositionStart, 0, preedit.Length));
            }
            else if (_client is { } client)
            {
                client.Selection = new EditorSelection(_selection.StartCaretPosition, _selection.EndCaretPosition);
            }

            args.Result = CoreTextSelectionUpdatingResult.Succeeded;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void HandleFormatUpdating(CoreTextEditContext sender, CoreTextFormatUpdatingEventArgs args)
    {
        args.Result = CoreTextFormatUpdatingResult.Succeeded;
    }

    private void HandleCompositionStarted(CoreTextEditContext sender, CoreTextCompositionStartedEventArgs args)
    {
        IsComposing = true;
        _hasPreedit = false;
        _compositionStart = _selection.StartCaretPosition;
        _compositionLength = _selection.EndCaretPosition - _selection.StartCaretPosition;
        _commitRange = _client?.Selection ?? default;
    }

    private void HandleCompositionCompleted(CoreTextEditContext sender, CoreTextCompositionCompletedEventArgs args)
    {
        if (_isEnding)
        {
            return;
        }

        var text = _imeText.Substring(_compositionStart, _compositionLength);
        _isUpdating = true;
        IsComposing = false;
        try
        {
            _client?.SetPreeditText(null, null);
            if (_client is { } client)
            {
                client.Selection = _commitRange;
                if (text.Length > 0)
                {
                    Commit(text);
                }
            }

            SynchronizeClient(false);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void HandleFocusRemoved(CoreTextEditContext sender, object args)
    {
        _hasFocus = false;
        if (!_isEnding)
        {
            FocusLost?.Invoke();
        }
    }
}
