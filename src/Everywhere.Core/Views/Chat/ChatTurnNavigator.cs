using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Everywhere.Chat;
using Everywhere.Utilities;

namespace Everywhere.Views;

/// <summary>
/// A drawn turn rail and a bounded preview overlay. Scrolling is a scalar offset, not a scroll
/// container; only the visible range plus two overscan marks is visited by the renderer.
/// </summary>
public sealed class ChatTurnNavigator : Decorator, ICustomHitTest
{
    /// <summary>
    /// Defines the animated presence of the turn previews.
    /// </summary>
    public static readonly DirectProperty<ChatTurnNavigator, double> PreviewPresenceProperty =
        AvaloniaProperty.RegisterDirect<ChatTurnNavigator, double>(
            nameof(PreviewPresence),
            static navigator => navigator.PreviewPresence);

    /// <summary>
    /// Gets the animated preview presence, from zero while hidden to one while fully shown.
    /// </summary>
    public double PreviewPresence
    {
        get;
        private set => SetAndRaise(PreviewPresenceProperty, ref field, value);
    }

    /// <summary>
    /// Defines the selected conversation.
    /// </summary>
    public static readonly StyledProperty<ChatContext?> ChatContextProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, ChatContext?>(nameof(ChatContext));

    /// <summary>
    /// Defines the message list receiving explicit turn navigation.
    /// </summary>
    public static readonly StyledProperty<ChatMessageItemsControl?> TargetProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, ChatMessageItemsControl?>(nameof(Target));

    /// <summary>
    /// Defines the user node currently intersecting the message viewport center.
    /// </summary>
    public static readonly StyledProperty<ChatMessageNode?> ReadingNodeProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, ChatMessageNode?>(nameof(ReadingNode));

    /// <summary>
    /// Gets or sets the selected conversation.
    /// </summary>
    public ChatContext? ChatContext
    {
        get => GetValue(ChatContextProperty);
        set => SetValue(ChatContextProperty, value);
    }

    /// <summary>
    /// Gets or sets the message list receiving explicit turn navigation.
    /// </summary>
    public ChatMessageItemsControl? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>
    /// Gets or sets the turn highlighted independently from pointer preview.
    /// </summary>
    public ChatMessageNode? ReadingNode
    {
        get => GetValue(ReadingNodeProperty);
        set => SetValue(ReadingNodeProperty, value);
    }

    /// <summary>
    /// Defines how turn navigation and its previews are presented.
    /// </summary>
    public static readonly StyledProperty<ChatTurnNavigationMode> ModeProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, ChatTurnNavigationMode>(nameof(Mode), ChatTurnNavigationMode.Fluid);

    /// <summary>
    /// Gets or sets how turn navigation and its previews are presented.
    /// </summary>
    public ChatTurnNavigationMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>
    /// Defines the theme line height used to budget preview slots before they are created.
    /// </summary>
    public static readonly StyledProperty<double> PreviewLineHeightProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, double>(nameof(PreviewLineHeight), 20);

    /// <summary>
    /// Gets or sets the theme line height used for preview capacity.
    /// </summary>
    public double PreviewLineHeight
    {
        get => GetValue(PreviewLineHeightProperty);
        set => SetValue(PreviewLineHeightProperty, value);
    }

    /// <summary>
    /// Defines the insets of the area available to the drawn rail, in DIPs.
    /// </summary>
    public static readonly StyledProperty<Thickness> LinePaddingProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, Thickness>(
            nameof(LinePadding),
            new Thickness(0, 16),
            validate: value => double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
                double.IsFinite(value.Right) && double.IsFinite(value.Bottom) &&
                value is { Left: >= 0, Top: >= 0, Right: >= 0, Bottom: >= 0 });

    /// <summary>
    /// Gets or sets rail padding without insetting previews or the backdrop.
    /// </summary>
    public Thickness LinePadding
    {
        get => GetValue(LinePaddingProperty);
        set => SetValue(LinePaddingProperty, value);
    }

    private const double Pitch = 12;
    private const double RailWidth = 40;
    private readonly ChatTurnPreviewPanel _previews;
    private readonly List<Wake> _wakes = [];
    private ChatTurnNavigationIndex? _index;
    private TopLevel? _topLevel;
    private TimeSpan? _lastFrame;
    private bool _isFramePending;
    private bool _isHovering;
    private int _attachmentVersion;
    private int _readingIndex = -1;
    private double _pointerY;
    private double _lastWakePosition = double.NaN;
    private Spring _focus;
    private Spring _offset;
    private Spring _presence;
    private Spring _previewY;

    private int TurnCount => _index?.Turns.Count ?? 0;
    private double RailAvailableHeight => Math.Max(0, Bounds.Height - LinePadding.Top - LinePadding.Bottom);
    private double RailHeight => Math.Min(400, RailAvailableHeight);
    private double MaximumOffset => Math.Max(0, (TurnCount - 1) * Pitch + 16 - RailHeight);
    private double Origin => RailClip.Top + Math.Max(8, (RailHeight - (TurnCount - 1) * Pitch) / 2);
    private double PointerPosition => Math.Clamp((_pointerY - Origin + _offset.Value) / Pitch, 0, Math.Max(0, TurnCount - 1));
    private double PreviewSlotHeight => PreviewLineHeight * 6 + 28;

    private Rect RailClip => new(
        Math.Min(LinePadding.Left, Bounds.Width),
        Math.Min(LinePadding.Top, Bounds.Height) + (RailAvailableHeight - RailHeight) / 2,
        Math.Min(RailWidth, Math.Max(0, Bounds.Width - LinePadding.Left - LinePadding.Right)),
        RailHeight);

    private Rect HitBounds
    {
        get
        {
            if (TurnCount == 0) return default;
            var firstY = Origin - _offset.Value;
            var lastY = firstY + (TurnCount - 1) * Pitch;
            var top = Math.Max(RailClip.Top, firstY - Pitch);
            var bottom = Math.Min(RailClip.Bottom, lastY + Pitch);
            return new Rect(RailClip.Left, top, RailClip.Width, Math.Max(0, bottom - top));
        }
    }

    /// <summary>
    /// Creates a rail and a lazy preview overlay.
    /// </summary>
    public ChatTurnNavigator()
    {
        _previews = new ChatTurnPreviewPanel(this);
        Child = _previews;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.PropertyChanged += HandleTopLevelPropertyChanged;
        Reconnect();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attachmentVersion++;
        if (_topLevel is not null) _topLevel.PropertyChanged -= HandleTopLevelPropertyChanged;
        _topLevel = null;
        _isFramePending = false;
        DisposeHelper.DisposeToDefault(ref _index);
        ClearMotion();

        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextElement.ForegroundProperty) InvalidateVisual();
        else if (change.Property == ChatContextProperty && _topLevel is not null) Reconnect();
        else if (change.Property == ReadingNodeProperty) UpdateReadingPosition();
        else if (change.Property == ModeProperty)
        {
            SetCurrentValue(IsVisibleProperty, Mode != ChatTurnNavigationMode.None);
            RequestFrame();
        }
        else if (change.Property == PreviewLineHeightProperty) RequestFrame();
        else if (change.Property == BoundsProperty || change.Property == LinePaddingProperty)
        {
            _offset.Target = Math.Clamp(_offset.Target, 0, MaximumOffset);
            _offset.Value = Math.Clamp(_offset.Value, 0, MaximumOffset);
            UpdateReadingPosition();
            if (_isHovering && !HitTest(new Point(RailClip.Center.X, _pointerY))) LeavePointer();
            RequestFrame();
        }
        else if (change.Property == IsVisibleProperty || change.Property == IsEnabledProperty)
        {
            if (!IsVisible || !IsEnabled) ClearMotion();
            else RequestFrame();
        }
    }

    private void Reconnect()
    {
        DisposeHelper.DisposeToDefault(ref _index);
        ClearMotion();

        if (ChatContext is { } context)
        {
            _index = new ChatTurnNavigationIndex(context);
            _index.Changed += HandleIndexChanged;
        }

        _offset = new Spring { Value = MaximumOffset, Target = MaximumOffset };
        HandleIndexChanged();
    }

    private void HandleIndexChanged()
    {
        _offset.Target = Math.Clamp(_offset.Target, 0, MaximumOffset);
        _offset.Value = Math.Clamp(_offset.Value, 0, MaximumOffset);
        UpdateReadingPosition();
        UpdateCards();
        InvalidateVisual();
        RequestFrame();
    }

    private void UpdateReadingPosition()
    {
        _readingIndex = -1;
        if (_index is not null)
        {
            for (var i = 0; i < _index.Turns.Count; i++)
            {
                if (!ReferenceEquals(_index.Turns[i].Node, ReadingNode)) continue;
                _readingIndex = i;
                break;
            }
        }

        // Reading follows the main viewport only outside pointer exploration. A new streamed
        // turn must not steal the rail offset from someone previewing older history.
        if (!_isHovering && _readingIndex >= 0)
        {
            var y = Origin + _readingIndex * Pitch - _offset.Target;
            var top = RailClip.Top + 8;
            var bottom = top + RailHeight - 16;
            if (y < top) _offset.Target -= top - y;
            else if (y > bottom) _offset.Target += y - bottom;
            _offset.Target = Math.Clamp(_offset.Target, 0, MaximumOffset);
        }

        InvalidateVisual();
        RequestFrame();
    }

    private void MovePointer(PointerEventArgs e)
    {
        _pointerY = e.GetPosition(this).Y;
        if (!_isHovering)
        {
            _focus = new Spring { Value = PointerPosition, Target = PointerPosition };
            _previewY = new Spring { Value = _pointerY, Target = _pointerY };
            _lastWakePosition = PointerPosition;
        }

        _isHovering = true;
        _presence.Target = 1;
        var position = PointerPosition;
        if (Math.Abs(position - _lastWakePosition) >= 2)
        {
            if (_wakes.Count == 4) _wakes.RemoveAt(0);
            _wakes.Add(new Wake(_lastWakePosition));
            _lastWakePosition = position;
        }

        _focus.Target = position;
        _previewY.Target = _pointerY;
        UpdateCards();
        RequestFrame();
    }

    private void LeavePointer()
    {
        _isHovering = false;
        _presence.Target = 0;
        RequestFrame();
    }

    private void Scroll(PointerWheelEventArgs e)
    {
        MovePointer(e);
        _offset.Target = Math.Clamp(_offset.Target - e.Delta.Y * Pitch * 3, 0, MaximumOffset);
        // Consume the wheel even at the ends: exploration must never scroll the conversation.
        e.Handled = true;
        RequestFrame();
    }

    private void Activate(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _index is null || TurnCount == 0) return;

        MovePointer(e);
        var node = _index.Turns[(int)Math.Round(PointerPosition)].Node;
        if (Target is { } target && ReferenceEquals(target.ChatContext, ChatContext)) target.RevealTurnAsync(node).Detach();
        e.Handled = true;
    }

    private void RequestFrame()
    {
        if (_isFramePending || _topLevel is null || !IsEffectivelyVisible || !IsEnabled) return;

        _isFramePending = true;
        var version = _attachmentVersion;
        _topLevel.RequestAnimationFrame(time =>
        {
            if (version != _attachmentVersion) return;
            _isFramePending = false;
            Animate(time);
        });
    }

    private void Animate(TimeSpan time)
    {
        if (_topLevel is null || !IsEffectivelyVisible || !IsEnabled)
        {
            ClearMotion();
            return;
        }

        var elapsed = _lastFrame is { } previous ? Math.Clamp((time - previous).TotalSeconds, 0, 0.05) : 1d / 60;
        _lastFrame = time;
        var isMoving = _offset.Step(elapsed, 22);
        if (_isHovering) _focus.Target = PointerPosition;
        isMoving |= _focus.Step(elapsed, 28);
        isMoving |= _presence.Step(elapsed, 22);
        PreviewPresence = Math.Clamp(_presence.Value, 0, 1);
        isMoving |= _previewY.Step(elapsed, 28);
        isMoving |= _previews.Step(elapsed);
        for (var i = _wakes.Count - 1; i >= 0; i--)
        {
            var wake = _wakes[i];
            wake.Age += elapsed;
            if (wake.Age > 0.45) _wakes.RemoveAt(i);
            else _wakes[i] = wake;
        }

        isMoving |= UpdateCards();
        InvalidateVisual();
        if (isMoving || _wakes.Count > 0) RequestFrame();
        else _lastFrame = null;
    }

    private bool UpdateCards()
    {
        if (_index is null || TurnCount == 0 || _presence.Value < 0.002 && !_isHovering)
        {
            _previews.Clear();
            return false;
        }

        var center = Math.Clamp(_focus.Value, 0, TurnCount - 1);
        var fittedNeighborCount = (int)((Bounds.Height - 16) / (PreviewSlotHeight + 8) - 1) / 2;
        var neighborCount = Math.Clamp(fittedNeighborCount + 1, 0, 3);
        return _previews.Update(_index, center, neighborCount, _presence.Value, _previewY.Value);
    }

    private void ClearMotion()
    {
        _isHovering = false;
        _lastFrame = null;
        _presence = default;
        PreviewPresence = 0;
        _focus = default;
        _previewY = default;
        _wakes.Clear();
        _previews.Clear();
    }

    private void HandleTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        if (e.NewValue is false) ClearMotion();
        else RequestFrame();
    }

    /// <inheritdoc />
    public bool HitTest(Point point) => TurnCount > 0 && RailHeight > 0 && RailClip.Width > 0 && HitBounds.Contains(point);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var clip = RailClip;
        if (clip.Height <= 0) return;

        var top = clip.Top;
        var origin = Origin - _offset.Value;
        var first = Math.Max(0, (int)Math.Floor((top - origin) / Pitch) - 2);
        var last = Math.Min(TurnCount - 1, (int)Math.Ceiling((clip.Bottom - origin) / Pitch) + 2);
        var brush = TextElement.GetForeground(this) ?? Brushes.Gray;
        var normalPen = new Pen(brush, 1.5, lineCap: PenLineCap.Round);
        var readingPen = new Pen(brush, 2, lineCap: PenLineCap.Round);
        var fadeLength = Math.Min(Pitch * 2, clip.Height / 2);
        // Fade only toward hidden content. Blend the fade strength out as the actual animated
        // offset reaches an end, instead of switching opacity abruptly at the scroll boundary.
        var topFade = Math.Clamp(_offset.Value / fadeLength, 0, 1);
        var bottomFade = Math.Clamp((MaximumOffset - _offset.Value) / fadeLength, 0, 1);
        using (context.PushClip(clip))
        {
            for (var i = first; i <= last; i++)
            {
                var influence = Math.Exp(-Math.Pow((i - _focus.Value) / 2.4, 2)) * _presence.Value;
                foreach (var wake in _wakes)
                {
                    var strength = 0.65 * Math.Pow(1 - wake.Age / 0.45, 2);
                    influence = Math.Max(influence, strength * Math.Exp(-Math.Pow((i - wake.Position) / 1.8, 2)));
                }

                var isReading = i == _readingIndex;
                var length = 4 + influence * 24;
                var y = origin + i * Pitch;
                var edge = (1 - topFade * (1 - Math.Clamp((y - top) / fadeLength, 0, 1))) * (1 - bottomFade * (1 - Math.Clamp((clip.Bottom - y) / fadeLength, 0, 1)));
                using (context.PushOpacity((0.24 + 0.7 * Math.Max(influence, isReading ? 0.7 : 0)) * edge))
                {
                    context.DrawLine(isReading ? readingPen : normalPen, new Point(clip.Left, y), new Point(clip.Left + length, y));
                }
            }
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        MovePointer(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        MovePointer(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        LeavePointer();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Scroll(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Activate(e);
    }

    private struct Wake(double position)
    {
        public double Position { get; } = position;
        public double Age { get; set; }
    }

    private struct Spring
    {
        public double Value { get; set; }
        public double Target { get; set; }

        private double _velocity;

        public void Reset(double value)
        {
            Value = value;
            _velocity = 0;
        }

        public bool Step(double elapsed, double frequency)
        {
            // Analytic critically damped spring: retarget without discarding velocity and remain
            // stable at different refresh rates. No layout properties participate in the motion.
            var displacement = Value - Target;
            var coefficient = _velocity + frequency * displacement;
            var decay = Math.Exp(-frequency * elapsed);
            Value = Target + (displacement + coefficient * elapsed) * decay;
            _velocity = (_velocity - frequency * coefficient * elapsed) * decay;

            if (Math.Abs(Value - Target) > 0.001 || Math.Abs(_velocity) > 0.001) return true;
            Value = Target;
            _velocity = 0;
            return false;
        }
    }


    /// <summary>
    /// Owns the small realized preview window. Child collection changes happen before layout;
    /// measure captures natural heights and arrange commits them as one coherent snapshot.
    /// </summary>
    internal sealed class ChatTurnPreviewPanel : Panel
    {
        private const double EdgeFadeLength = 6;
        private const double PreviewGap = 12;
        private const double PreviewLeft = 48;
        private const int RealizationBuffer = 1;
        private readonly ChatTurnNavigator _owner;
        private readonly Dictionary<ChatTurnNavigationIndex.PreviewItem, PreviewState> _items = [];
        private readonly List<PreviewState> _orderedItems = [];
        private readonly Stack<ChatTurnPreviewItemPresenter> _recyclePool = [];
        private readonly GradientStop _topOpaqueStop = new(Colors.White, 0);
        private readonly GradientStop _bottomOpaqueStop = new(Colors.White, 1);
        private bool _preserveItemPositions;

        public ChatTurnPreviewPanel(ChatTurnNavigator owner)
        {
            _owner = owner;
            // Composition resolves a visual opacity mask against its rendered subtree bounds.
            // A transparent background pins that subtree to this panel's viewport as cards move.
            Background = Brushes.Transparent;
            ClipToBounds = true;
            IsHitTestVisible = false;
            OpacityMask = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    _topOpaqueStop,
                    _bottomOpaqueStop,
                    new GradientStop(Colors.Transparent, 1)
                }
            };
        }

        public bool Update(ChatTurnNavigationIndex index, double center, int neighborCount, double presence, double requestedY)
        {
            EnsureRealizedItems(index, center, neighborCount);
            return UpdateTransforms(index, center, presence, requestedY);
        }

        public bool Step(double elapsed)
        {
            return _orderedItems.AsValueEnumerable().Aggregate(false, (current, state) => current | state.LayoutCorrection.Step(elapsed, 32));
        }

        public void Clear()
        {
            _items.Clear();
            _orderedItems.Clear();
            _recyclePool.Clear();
            while (Children.Count > 0) Children.RemoveAt(Children.Count - 1);
            _preserveItemPositions = false;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var previewWidth = GetPreviewWidth(availableSize.Width);
            var constraint = new Size(previewWidth, double.PositiveInfinity);
            foreach (var state in _orderedItems) state.Presenter.Measure(constraint);

            var geometryChanged = false;
            var hasNewMeasurements = false;
            foreach (var state in _orderedItems)
            {
                var height = state.Presenter.DesiredSize.Height;
                geometryChanged |= state.IsMeasured && Math.Abs(state.Height - height) > 0.01;
                hasNewMeasurements |= !state.IsMeasured;
            }
            foreach (var state in _orderedItems)
            {
                state.Height = state.Presenter.DesiredSize.Height;
                state.IsMeasured = true;
            }

            if (geometryChanged) _preserveItemPositions = true;
            if (geometryChanged || hasNewMeasurements) _owner.RequestFrame();

            // This panel is an overlay and must not reserve space in its parent.
            return default;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            UpdateOpacityMask(finalSize.Height);
            var previewWidth = GetPreviewWidth(finalSize.Width);
            foreach (var state in _orderedItems)
                state.Presenter.Arrange(new Rect(0, 0, previewWidth, state.Height));
            return finalSize;
        }

        private void EnsureRealizedItems(ChatTurnNavigationIndex index, double center, int neighborCount)
        {
            var firstTurn = Math.Max(0, (int)Math.Floor(center) - neighborCount);
            var lastTurn = Math.Min(index.Turns.Count - 1, (int)Math.Ceiling(center) + neighborCount);

            // A fixed turn window decides what to create. Items that have left that window stay
            // alive until their transformed bounds leave the viewport. Fold those items into the
            // required range before adding the buffer so a measured item is always waiting beyond
            // either visible edge, even though Fluid distance scaling never drops below 50 percent.
            foreach (var state in _items.Values)
            {
                var itemIndex = state.Item.PreviewIndex;
                if (!ReferenceEquals(state.Item.Index, index) ||
                    (uint)itemIndex >= (uint)index.PreviewItems.Count ||
                    !ReferenceEquals(index.PreviewItems[itemIndex], state.Item) ||
                    !state.HasRendered ||
                    state.LastRenderedY + state.VisibleHeight <= 0 ||
                    state.LastRenderedY >= Bounds.Height) continue;

                firstTurn = Math.Min(firstTurn, state.Item.TurnIndex);
                lastTurn = Math.Max(lastTurn, state.Item.TurnIndex);
            }

            firstTurn = Math.Max(0, firstTurn - RealizationBuffer);
            lastTurn = Math.Min(index.Turns.Count - 1, lastTurn + RealizationBuffer);
            var first = index.Turns[firstTurn].PreviewIndex;
            if (first > 0 && index.PreviewItems[first - 1] is ChatTurnNavigationIndex.DateHeader) first--;
            var last = index.Turns[lastTurn].PreviewIndex;

            var rangeChanged = _orderedItems.Count == 0 || _orderedItems[0].Index != first || _orderedItems[^1].Index != last;
            if (!rangeChanged)
            {
                var matches = true;
                for (var i = first; i <= last; i++) matches &= ReferenceEquals(_orderedItems[i - first].Item, index.PreviewItems[i]);
                if (matches) return;
            }

            foreach (var pair in _items.ToArray())
            {
                var keep = false;
                for (var i = first; i <= last; i++) keep |= ReferenceEquals(index.PreviewItems[i], pair.Key);
                if (keep) continue;

                pair.Value.Presenter.Item = null;
                Children.Remove(pair.Value.Presenter);
                _recyclePool.Push(pair.Value.Presenter);
                _items.Remove(pair.Key);
            }

            _orderedItems.Clear();
            for (var i = first; i <= last; i++)
            {
                var item = index.PreviewItems[i];
                if (!_items.TryGetValue(item, out var state))
                {
                    var preview = GetPresenter();
                    state = new PreviewState(preview, item, i);
                    _items.Add(item, state);
                    Children.Add(preview);
                    preview.Item = item;
                }
                state.Index = i;
                _orderedItems.Add(state);
            }
        }

        private ChatTurnPreviewItemPresenter GetPresenter()
        {
            if (_recyclePool.TryPop(out var recycled)) return recycled;

            var preview = new ChatTurnPreviewItemPresenter
            {
                CacheMode = new BitmapCache(),
                RenderTransform = new MatrixTransform(),
                RenderTransformOrigin = RelativePoint.TopLeft
            };
            if (preview.RenderTransform is MatrixTransform transform)
            {
                transform.Matrix = Matrix.CreateScale(0.001, 0.001);
            }

            return preview;
        }

        private bool UpdateTransforms(ChatTurnNavigationIndex index, double center, double presence, double requestedY)
        {
            if (_orderedItems.Count == 0) return false;

            var isFluid = _owner.Mode == ChatTurnNavigationMode.Fluid;
            var lowerTurnIndex = Math.Clamp((int)Math.Floor(center), 0, index.Turns.Count - 1);
            var lowerState = _items[index.Turns[lowerTurnIndex]];
            var upperState = lowerTurnIndex + 1 < index.Turns.Count ? _items[index.Turns[lowerTurnIndex + 1]] : null;
            var presenceScale = Math.Clamp(presence * 3, 0, 1);
            foreach (var state in _orderedItems)
            {
                state.Distance = state.Item.TurnIndex - center;
                var magnitude = Math.Abs(state.Distance);
                var distanceScale = Math.Max(0.5, 1 - 0.1 * magnitude);
                var scale = isFluid ? distanceScale * presenceScale : 1;
                state.Scale = state.IsMeasured ? scale : 0;
                state.VisibleHeight = state.Height * state.Scale;
                state.Presenter.Opacity = state.IsMeasured ? isFluid ? 1 : presenceScale : 0;
            }

            _orderedItems[0].CenterY = 0;
            for (var i = 1; i < _orderedItems.Count; i++)
            {
                _orderedItems[i].CenterY = _orderedItems[i - 1].CenterY + GetInterval(_orderedItems[i - 1], _orderedItems[i]);
            }

            var fraction = center - lowerTurnIndex;
            var focusCenter = lowerState.CenterY;
            if (upperState is not null) focusCenter += (upperState.CenterY - focusCenter) * fraction;
            foreach (var state in _orderedItems) state.CenterY -= focusCenter;

            var anchorY = isFluid ? Math.Clamp(requestedY, 0, Bounds.Height) : GetSimplePreviewAnchor(lowerState, upperState, fraction, requestedY);
            var preservePositions = _preserveItemPositions;
            var isMoving = false;
            foreach (var state in _orderedItems)
            {
                if (!state.IsMeasured) continue;

                var targetY = anchorY + state.CenterY - state.VisibleHeight / 2;
                if (preservePositions && state.HasRendered)
                {
                    state.LayoutCorrection.Reset(state.LastRenderedY - targetY);
                    isMoving = true;
                }

                var y = targetY + state.LayoutCorrection.Value;
                state.LastRenderedY = y;
                state.HasRendered = true;
                state.Presenter.ZIndex = 10 - (int)(Math.Abs(state.Distance) * 2);
                if (state.Presenter.RenderTransform is MatrixTransform transform)
                {
                    if (isFluid)
                    {
                        var renderScale = Math.Max(0.001, state.Scale);
                        transform.Matrix = Matrix.CreateScale(renderScale, renderScale) * Matrix.CreateTranslation(PreviewLeft - (1 - presence) * 24, y);
                    }
                    else
                    {
                        transform.Matrix = Matrix.CreateTranslation(PreviewLeft, y);
                    }
                }
            }

            _preserveItemPositions = false;
            return isMoving;
        }

        private double GetSimplePreviewAnchor(PreviewState lowerState, PreviewState? upperState, double fraction, double requestedY)
        {
            // Interpolate between turn cards; date headings between them participate in spacing
            // but must not become the pointer anchor themselves.
            var height = lowerState.Height;
            if (upperState is not null) height += (upperState.Height - height) * fraction;

            // Collapse the permitted range continuously to the viewport center if it is too small.
            var inset = Math.Min(height / 2 + 8, Bounds.Height / 2);
            return Math.Clamp(requestedY, inset, Bounds.Height - inset);
        }

        private void UpdateOpacityMask(double height)
        {
            var offset = height > 0 ? Math.Min(0.5, EdgeFadeLength / height) : 0.5;
            if (Math.Abs(_topOpaqueStop.Offset - offset) < 0.001) return;

            _topOpaqueStop.Offset = offset;
            _bottomOpaqueStop.Offset = 1 - offset;
        }

        private static double GetInterval(PreviewState first, PreviewState second) =>
            first.VisibleHeight / 2 + PreviewGap + second.VisibleHeight / 2;

        private static double GetPreviewWidth(double availableWidth) =>
            Math.Max(0, Math.Min(320, availableWidth - PreviewLeft - 16));

        private sealed class PreviewState(ChatTurnPreviewItemPresenter presenter, ChatTurnNavigationIndex.PreviewItem item, int index)
        {
            public ChatTurnPreviewItemPresenter Presenter { get; } = presenter;
            public ChatTurnNavigationIndex.PreviewItem Item { get; } = item;
            public int Index { get; set; } = index;
            public double Height { get; set; }
            public double LastRenderedY { get; set; }
            public double Distance { get; set; }
            public double Scale { get; set; }
            public double VisibleHeight { get; set; }
            public double CenterY { get; set; }
            public bool IsMeasured { get; set; }
            public bool HasRendered { get; set; }
            public Spring LayoutCorrection { get; } = new();
        }
    }
}