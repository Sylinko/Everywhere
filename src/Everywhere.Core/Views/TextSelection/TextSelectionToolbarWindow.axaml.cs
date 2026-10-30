using Avalonia.Controls;
using Avalonia.Input;
#if MACOS
using Avalonia.Native;
#endif
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.StrategyEngine;
using Everywhere.TextSelection;
using Serilog;
using TextSelectionToolbarAction = Everywhere.TextSelection.TextSelectionToolbarAction;

namespace Everywhere.Views;

/// <summary>
/// Owns the floating toolbar's presentation, placement and input lifetime. Selection ownership and
/// strategy execution remain with the service; this window never takes ownership of an attachment.
/// </summary>
public partial class TextSelectionToolbarWindow : Window
{
    /// <summary>
    /// Vertical gap between the anchor point and the toolbar, in physical pixels.
    /// </summary>
    private const int PointerOffset = 16;

    /// <summary>
    /// Minimum distance kept between the toolbar and the edge of the working area, in physical pixels.
    /// </summary>
    private const int EdgeMargin = 6;

    /// <summary>
    /// Where the window is parked while preparing its first frame for each appearance, so the
    /// previous selection's retained surface is never revealed before the new content is rendered.
    /// </summary>
    private static readonly PixelPoint OffScreenPosition = new(-32000, -32000);

    /// <summary>
    /// Gets the settings source bound by the window, independently of the selection service.
    /// </summary>
    public Settings Settings { get; }

    /// <summary>
    /// Submits a freeform draft to its business owner. True transfers the selection to the chat;
    /// false retains the draft for retry. Invocation is synchronous and occurs after releasing input.
    /// </summary>
    public Func<string, CustomAssistant, bool>? SubmitFreeformChat { get; set; }

    /// <summary>
    /// Sends the current editor text through the host while keeping rejection non-destructive.
    /// </summary>
    public IRelayCommand<string> SendFreeformChatCommand { get; }

    /// <summary>
    /// Identifies the pointer-distance factor applied to the user's configured opacity.
    /// </summary>
    public static readonly DirectProperty<TextSelectionToolbarWindow, double> OpacityFactorProperty =
        AvaloniaProperty.RegisterDirect<TextSelectionToolbarWindow, double>(nameof(OpacityFactor), o => o.OpacityFactor);

    /// <summary>
    /// Gets the quantized distance factor. The binding applies the user's opacity separately.
    /// </summary>
    public double OpacityFactor
    {
        get;
        private set => SetAndRaise(OpacityFactorProperty, ref field, value);
    } = 1d;

    /// <summary>
    /// Raised when the user picks an action. The window releases input before notifying the service.
    /// </summary>
    public event Action<Strategy>? ActionInvoked;

    /// <summary>
    /// Requests exclusion using the service's current selection identity, not the foreground window.
    /// </summary>
    public event Action? ExcludeApplicationRequested;

    /// <summary>
    /// Raised after the window is hidden and its input and presentation have been released.
    /// The service can release its selection without calling back into the window.
    /// </summary>
    public event Action? Hidden;

    private readonly IWindowHelper _windowHelper;
    private readonly IOverlayInputWatcher _inputWatcher;

    private PixelPoint _anchor;
    private PixelRect _toolbarBounds;
    private PixelRect? _menuBounds;
    private IReadOnlyList<TextSelectionToolbarItem> _items = [];
    private bool _isPreparingPresentation;

    // Published on the UI thread; captures geometry without exposing mutable UI state to native hooks.
    private Action<PixelPoint>? _updateOpacityFactor;

    public TextSelectionToolbarWindow(IWindowHelper windowHelper, IOverlayInputWatcher inputWatcher, Settings settings)
#if MACOS
        : base(AvaloniaNativeWindowFactory.CreatePanel(new AvaloniaNativePanelOptions
        {
            IsNonActivating = true
        }))
#endif
    {
        Settings = settings;
        _windowHelper = windowHelper;
        _inputWatcher = inputWatcher;
        SendFreeformChatCommand = new RelayCommand<string>(SendFreeformChat, CanSendFreeformChat);
        InitializeComponent();

        windowHelper.SetWindowProperties(this, inputMode: WindowInputMode.NonActivating, layer: WindowLayer.Overlay);
        ToolbarRoot.ActionInvoked += HandleActionInvoked;
        ToolbarRoot.FreeformChatInputRequested += HandleFreeformChatInputRequested;
        ToolbarRoot.FreeformChatExiting += HandleFreeformChatExiting;
        ToolbarRoot.MenuBoundsChanged += HandleMenuBoundsChanged;
        ToolbarRoot.CloseRequested += Hide;
        ToolbarRoot.ExcludeApplicationRequested += () => ExcludeApplicationRequested?.Invoke();
        SizeChanged += (_, _) => UpdateBounds();
        ScalingChanged += (_, _) => UpdateBounds();
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
#if LINUX
        Deactivated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Native Wayland editing uses ordinary activation. Candidate and
            // assistant popups stay part of this interaction; leaving the editor
            // for another application ends the palette appearance.
            if (_isUsingActivatedInput && IsVisible && !IsActive && _menuBounds is null)
            {
                Hide();
            }
        }, DispatcherPriority.Input);
#endif
    }

    private bool CanSendFreeformChat(string? text)
    {
        return IsVisible && ToolbarRoot.IsFreeformChat && !string.IsNullOrWhiteSpace(text) &&
            ToolbarRoot.SelectedCustomAssistant is not null && SubmitFreeformChat is not null &&
            _inputEditor is not null && !IsInputComposing &&
            (_isUsingActivatedInput || _inputCapture is { IsActive: true });
    }

    private void SendFreeformChat(string? text)
    {
        if (!CanSendFreeformChat(text) || text is null ||
            ToolbarRoot.SelectedCustomAssistant is not { } assistant || SubmitFreeformChat is not { } submit)
        {
            return;
        }

        var editor = _inputEditor;
        EndTextInput();
        if (submit(text.Trim(), assistant))
        {
            Hide();
        }
        else if (IsVisible && ToolbarRoot.IsFreeformChat && editor is not null)
        {
            // A busy destination has not consumed anything. Restore this appearance's existing
            // prepared route or ordinary activated input, without creating a new native host.
            HandleFreeformChatInputRequested(editor);
        }
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None ||
            !ToolbarRoot.IsFreeformChat || IsInputComposing)
        {
            return;
        }

        ToolbarRoot.HandleEscape();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The window lives for the lifetime of the application; only a real shutdown may close it.
        if (e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Prepares the toolbar and reveals it near the selection after its first updated frame.
    /// Keeps its matched strategy snapshot until hidden.
    /// Returns false when neither a strategy action nor enabled freeform input is available.
    /// </summary>
    /// <param name="items">Available tools; may be empty when freeform input is enabled.</param>
    /// <param name="anchor">Anchor point in physical screen pixels, normally the mouse pointer.</param>
    /// <param name="canExcludeApplication">Whether the selection has an application identity that can be excluded.</param>
    public bool ShowFor(IReadOnlyList<TextSelectionToolbarItem> items, PixelPoint anchor, bool canExcludeApplication)
    {
        Dispatcher.UIThread.VerifyAccess();
        ResetPresentation();

        // This is deliberately earlier than the user's switch to the editor. The UI must keep
        // the editor out of the visual tree in toolbar mode, not merely hide an already focused
        // input control. Preparing the native host must not create a CoreText edit context.
        _windowHelper.SetWindowProperties(this, inputMode: WindowInputMode.NonActivatingTextInput);
        _isInputPrepared = _windowHelper.TryPrepareInput(this);

        if (items.Count == 0 && !Settings.TextSelection.IsFreeformChatEnabled)
        {
            Hide();
            return false;
        }

        _anchor = anchor;
        _items = items;
        ToolbarRoot.Actions = items.AsValueEnumerable().Select(item => item.Action).ToArray();
        ToolbarRoot.CanExcludeApplication = canExcludeApplication;

        // Start each selection from the main assistant, but keep later toolbar choices local.
        // A two-way binding here would unexpectedly change the main chat's assistant.
        ToolbarRoot.SelectedCustomAssistant = Settings.Model.SelectedCustomAssistant;

        var transitions = ToolbarRoot.Transitions;
        ToolbarRoot.SetCurrentValue(TransitionsProperty, null);
        try
        {
            // Initialize the bound opacity before rendering, without fading from a previous selection.
            OpacityFactor = 1d;
            _isPreparingPresentation = true;
#if LINUX
            PresentationRoot.Opacity = 0;
            // Wayland compositors can constrain off-screen toplevels back to the
            // output center. Prepare at the real anchor, with transparent content
            // until both its first frame and its native placement are ready.
            ToolbarRoot.Measure(Size.Infinity);
            Reposition(_anchor, PixelSize.FromSize(ToolbarRoot.DesiredSize, DesktopScaling));
#else
            Position = OffScreenPosition;
#endif
            if (!IsVisible)
            {
                Show();
            }

            // Layout alone does not replace the retained native surface. Keep the
            // content transparent until the new actions have rendered; placement
            // callbacks must not reveal the previous editor during preparation.
            _toolbarBounds = new PixelRect(Position, MeasuredPixelSize());
            StartWatchingInput();
            if (ElementComposition.GetElementVisual(this) is not { } visual)
            {
                Hide();
                return false;
            }

            var rendered = visual.Compositor.RequestCompositionBatchCommitAsync().Rendered;
            RevealAfterRenderAsync(rendered, _generation).Detach();
            return true;
        }
        finally
        {
            ToolbarRoot.SetCurrentValue(TransitionsProperty, transitions);
        }
    }

    private async Task RevealAfterRenderAsync(Task rendered, int generation)
    {
        try
        {
            await rendered.ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => RevealAsync(generation), DispatcherPriority.Render);
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _generation || !IsVisible)
                {
                    return;
                }
                Log.Warning(exception, "Failed to prepare the text selection toolbar before revealing it");
                Hide();
            });
        }
    }

    private async Task RevealAsync(int generation)
    {
        if (generation != _generation || !IsVisible)
        {
            return;
        }
        var transitions = ToolbarRoot.Transitions;
        ToolbarRoot.SetCurrentValue(TransitionsProperty, null);
        try
        {
            Reposition(_anchor, MeasuredPixelSize());
            // Moving to the destination output can change scale and measurement.
            _toolbarBounds = Reposition(_anchor, MeasuredPixelSize());
            if (PlatformImpl?.TryGetFeature<IWindowPresentation>() is { } presentation)
            {
                await presentation.WaitForPlacementAsync(_toolbarBounds);
            }
            if (generation != _generation || !IsVisible)
            {
                return;
            }
            _isPreparingPresentation = false;
            PresentationRoot.Opacity = 1;
            _windowHelper.RaiseWindow(this);
            UpdateInputWatch();
            UpdateOpacityTracking();
        }
        finally
        {
            ToolbarRoot.SetCurrentValue(TransitionsProperty, transitions);
        }
    }

    /// <inheritdoc />
    public override void Hide()
    {
        // Native input must end before the HWND disappears. The visibility change then resets
        // presentation and notifies the service. Also clean appearances that never became visible.
        EndTextInput();
        if (!IsVisible)
        {
            ResetPresentation();
        }

        base.Hide();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            ResetPresentation();
            Hidden?.Invoke();
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        ResetPresentation();
        base.OnClosed(e);
    }

    private void ResetPresentation()
    {
        StopWatchingInput();
        EndTextInput();
        _isInputPrepared = false;
        _isUsingActivatedInput = false;
        _isPreparingPresentation = false;
        _menuBounds = null;
        _items = [];

        // Visibility can change during construction, before InitializeComponent creates the root.
        if (ToolbarRoot is { } toolbar)
        {
            toolbar.ResetFreeformChat();
            toolbar.CloseMenu();
            toolbar.Actions = null;
        }
    }

    private void HandleActionInvoked(TextSelectionToolbarAction action)
    {
        // Execute the snapshot matched for this selection, not a strategy reconstructed from preview metadata.
        var item = _items.FirstOrDefault(item => ReferenceEquals(item.Action, action));
        if (item is not null)
        {
            // The hidden host must release the keyboard before strategy execution activates the main chat.
            EndTextInput();
            ActionInvoked?.Invoke(item.Strategy);
        }
    }

    private void HandleMenuBoundsChanged(PixelRect? bounds)
    {
        _menuBounds = bounds;
        UpdateInputWatch();
        UpdateOpacityTracking();
    }

    private void UpdateBounds()
    {
        if (!IsVisible || _isPreparingPresentation)
        {
            return;
        }

        _toolbarBounds = Reposition(_anchor, PixelSize.FromSize(Bounds.Size, DesktopScaling));
        UpdateInputWatch();
        UpdateOpacityTracking();
    }

    private void UpdateOpacityTracking()
    {
        if (!IsVisible || _isPreparingPresentation)
        {
            return;
        }

        if (_inputWatch is null || !Settings.TextSelection.IsToolbarOpacityAdaptive)
        {
            Volatile.Write(ref _updateOpacityFactor, null);
            OpacityFactor = 1d;
            return;
        }

        // Bounds and scaling change on the UI thread. Publish only their copied values to the hook;
        // each revision owns its latest step so an old queued update cannot overwrite a new appearance.
        var scale = DesktopScaling;
        var bounds = _toolbarBounds.ToRect(scale);
        var menuBounds = _menuBounds?.ToRect(scale);
        var opacityStep = CalculateStep(_inputWatcher.PointerPosition ?? _anchor);
        var isUpdateQueued = 0;

        Volatile.Write(ref _updateOpacityFactor, UpdateFactor);
        OpacityFactor = opacityStep / 20d;

        int CalculateStep(PixelPoint pointerPoint)
        {
            var point = new Point(pointerPoint.X / scale, pointerPoint.Y / scale);
            return (int)Math.Round(TextSelectionToolbar.CalculateOpacityFactor(point, bounds, menuBounds) * 20d);
        }

        void UpdateFactor(PixelPoint position)
        {
            var step = CalculateStep(position);
            // ReSharper disable once AccessToModifiedClosure
            if (Interlocked.Exchange(ref opacityStep, step) == step)
            {
                return;
            }

            if (Interlocked.Exchange(ref isUpdateQueued, 1) != 0)
            {
                return;
            }

            // Quantize before dispatching, with at most one pending UI update per geometry revision.
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref isUpdateQueued, 0);
                if (_updateOpacityFactor != UpdateFactor || !IsVisible)
                {
                    return;
                }

                OpacityFactor = Volatile.Read(ref opacityStep) / 20d;
            });
        }
    }

    /// <summary>
    /// The current content size in physical pixels, after forcing layout to settle.
    /// </summary>
    private PixelSize MeasuredPixelSize()
    {
        UpdateLayout();

        var size = PixelSize.FromSize(Bounds.Size, DesktopScaling);

        // Guard against a degenerate measurement: better to fall back to the natural size of the
        // content than to hand out an empty rectangle.
        if (size is { Width: > 0, Height: > 0 }) return size;

        ToolbarRoot.Measure(Size.Infinity);
        var desired = ToolbarRoot.DesiredSize;
        return new PixelSize(
            Math.Max(1, (int)Math.Ceiling(desired.Width * DesktopScaling)),
            Math.Max(1, (int)Math.Ceiling(desired.Height * DesktopScaling)));
    }

    /// <summary>
    /// Places the toolbar below the anchor, flipping above it when there is no room, and clamps the
    /// result into the working area of the screen containing the anchor.
    /// </summary>
    private PixelRect Reposition(PixelPoint anchor, PixelSize size)
    {
        // Pick the screen from the anchor, not the primary screen: with multiple monitors the
        // selection may be anywhere, and each monitor may have its own scaling and working area.
        var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        var workingArea = screen?.WorkingArea ?? new PixelRect(anchor, size);

        var x = anchor.X - size.Width / 2;
        var y = anchor.Y + PointerOffset;

        // Flip above the anchor when the preferred placement would run off the bottom edge.
        if (y + size.Height + EdgeMargin > workingArea.Bottom)
        {
            y = anchor.Y - PointerOffset - size.Height;
        }

        x = Math.Clamp(x, workingArea.X + EdgeMargin, Math.Max(workingArea.X + EdgeMargin, workingArea.Right - size.Width - EdgeMargin));
        y = Math.Clamp(y, workingArea.Y + EdgeMargin, Math.Max(workingArea.Y + EdgeMargin, workingArea.Bottom - size.Height - EdgeMargin));

        Position = new PixelPoint(x, y);
        return new PixelRect(Position, size);
    }
}