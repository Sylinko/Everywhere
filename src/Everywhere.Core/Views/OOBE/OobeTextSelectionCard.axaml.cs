using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Everywhere.Common;
using Everywhere.TextSelection;
using Everywhere.Utilities;
using Lucide.Avalonia;

namespace Everywhere.Views;

/// <summary>
/// Interactive onboarding demonstration for text selection, presenting a single selectable document card
/// with a floating <see cref="TextSelectionToolbar"/> anchored directly above the active selection range.
/// </summary>
public sealed partial class OobeTextSelectionCard : UserControl
{
    /// <summary>
    /// Defines the <see cref="Flow"/> property.
    /// </summary>
    public static readonly StyledProperty<WelcomeFlow?> FlowProperty =
        AvaloniaProperty.Register<OobeTextSelectionCard, WelcomeFlow?>(nameof(Flow));

    /// <summary>
    /// Defines the <see cref="HasActionPreview"/> property.
    /// </summary>
    public static readonly DirectProperty<OobeTextSelectionCard, bool> HasActionPreviewProperty =
        AvaloniaProperty.RegisterDirect<OobeTextSelectionCard, bool>(
            nameof(HasActionPreview),
            o => o.HasActionPreview);

    /// <summary>
    /// Defines the <see cref="PreviewActionIcon"/> property.
    /// </summary>
    public static readonly DirectProperty<OobeTextSelectionCard, ColoredIcon?> PreviewActionIconProperty =
        AvaloniaProperty.RegisterDirect<OobeTextSelectionCard, ColoredIcon?>(
            nameof(PreviewActionIcon),
            o => o.PreviewActionIcon);

    /// <summary>
    /// Defines the <see cref="PreviewActionHeaderKey"/> property.
    /// </summary>
    public static readonly DirectProperty<OobeTextSelectionCard, IDynamicLocaleKey?> PreviewActionHeaderKeyProperty =
        AvaloniaProperty.RegisterDirect<OobeTextSelectionCard, IDynamicLocaleKey?>(
            nameof(PreviewActionHeaderKey),
            o => o.PreviewActionHeaderKey);

    /// <summary>
    /// Defines the <see cref="PreviewActionBodyKey"/> property.
    /// </summary>
    public static readonly DirectProperty<OobeTextSelectionCard, IDynamicLocaleKey?> PreviewActionBodyKeyProperty =
        AvaloniaProperty.RegisterDirect<OobeTextSelectionCard, IDynamicLocaleKey?>(
            nameof(PreviewActionBodyKey),
            o => o.PreviewActionBodyKey);

    /// <summary>
    /// Gets or sets the onboarding flow driving the text-selection step.
    /// </summary>
    public WelcomeFlow? Flow
    {
        get => GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    public bool HasActionPreview
    {
        get;
        private set => SetAndRaise(HasActionPreviewProperty, ref field, value);
    }

    public ColoredIcon? PreviewActionIcon
    {
        get;
        private set => SetAndRaise(PreviewActionIconProperty, ref field, value);
    }

    public IDynamicLocaleKey? PreviewActionHeaderKey
    {
        get;
        private set => SetAndRaise(PreviewActionHeaderKeyProperty, ref field, value);
    }

    public IDynamicLocaleKey? PreviewActionBodyKey
    {
        get;
        private set => SetAndRaise(PreviewActionBodyKeyProperty, ref field, value);
    }

    private const int MaxPreviewSnippetLength = 42;

    private static readonly Transitions ToolbarPositionTransitions =
    [
        new DoubleTransition
        {
            Property = TranslateTransform.XProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut()
        },
        new DoubleTransition
        {
            Property = TranslateTransform.YProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut()
        }
    ];

    private readonly TranslateTransform _toolbarTransform = new();
    private IDisposable? _actionsSubscription;
    private bool _isPointerSelecting;
    private bool _hasToolbarPosition;

    /// <summary>
    /// Initializes a new instance of the <see cref="OobeTextSelectionCard"/> class.
    /// </summary>
    public OobeTextSelectionCard()
    {
        InitializeComponent();
        DemoToolbar.RenderTransform = _toolbarTransform;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FlowProperty)
        {
            BindToolbarActions();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _hasToolbarPosition = false;
        _toolbarTransform.Transitions = null;

        SampleDocumentText.PropertyChanged += HandleSampleDocumentPropertyChanged;
        SampleDocumentText.AddHandler(PointerPressedEvent, HandleSamplePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SampleDocumentText.AddHandler(PointerReleasedEvent, HandleSamplePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        SampleDocumentText.AddHandler(PointerCaptureLostEvent, HandleSamplePointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        SampleDocumentText.LayoutUpdated += HandleStageLayoutUpdated;
        DemoToolbar.LayoutUpdated += HandleStageLayoutUpdated;
        DemoToolbar.ActionInvoked += HandleToolbarActionInvoked;
        DemoToolbar.CloseRequested += HandleToolbarCloseRequested;

        BindToolbarActions();
        SelectSamplePhrase();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposeHelper.DisposeToDefault(ref _actionsSubscription);

        SampleDocumentText.PropertyChanged -= HandleSampleDocumentPropertyChanged;
        SampleDocumentText.RemoveHandler(PointerPressedEvent, HandleSamplePointerPressed);
        SampleDocumentText.RemoveHandler(PointerReleasedEvent, HandleSamplePointerReleased);
        SampleDocumentText.RemoveHandler(PointerCaptureLostEvent, HandleSamplePointerCaptureLost);
        SampleDocumentText.LayoutUpdated -= HandleStageLayoutUpdated;
        DemoToolbar.LayoutUpdated -= HandleStageLayoutUpdated;
        DemoToolbar.ActionInvoked -= HandleToolbarActionInvoked;
        DemoToolbar.CloseRequested -= HandleToolbarCloseRequested;
        DemoToolbar.Actions = null;
        DemoToolbar.CloseMenu();

        base.OnDetachedFromVisualTree(e);
    }

    private void BindToolbarActions()
    {
        DisposeHelper.DisposeToDefault(ref _actionsSubscription);

        if (Flow?.TextSelectionActionsManager is { } actionsManager)
        {
            actionsManager.EnsureBuiltInActions();
            DemoToolbar.Actions = actionsManager.Items
                .ToObservableChangeSet()
                .AutoRefresh(static action => action.IsEnabled)
                .AutoRefresh(static action => action.IsAvailable)
                .Filter(static action => action is { IsEnabled: true, IsAvailable: true })
                .BindEx(out _actionsSubscription);
            return;
        }

        DemoToolbar.Actions = CreateFallbackDemoActions();
    }

    private void SelectSamplePhrase()
    {
        var fullText = SampleDocumentText.Text;
        if (string.IsNullOrEmpty(fullText))
        {
            UpdateToolbarPresentation();
            return;
        }

        var phrase = LocaleKey.Oobe_TextSelection_DemoHighlight.I18N();
        var startIndex = fullText.IndexOf(phrase, StringComparison.Ordinal);
        if (startIndex >= 0)
        {
            SampleDocumentText.SelectionStart = startIndex;
            SampleDocumentText.SelectionEnd = startIndex + phrase.Length;
        }
        else
        {
            SampleDocumentText.SelectionStart = 0;
            SampleDocumentText.SelectionEnd = fullText.Length;
        }

        UpdateToolbarPresentation();
    }

    private void HandleSamplePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(SampleDocumentText).Properties;
        if (!properties.IsLeftButtonPressed && properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

        _isPointerSelecting = true;
        _hasToolbarPosition = false;
        DemoToolbar.Classes.Set("visible", false);
        DemoToolbar.CloseMenu();
        ClearActionPreview();
    }

    private void HandleSamplePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPointerSelecting) return;

        _isPointerSelecting = false;
        UpdateToolbarPresentation();
    }

    private void HandleSamplePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isPointerSelecting) return;

        _isPointerSelecting = false;
        UpdateToolbarPresentation();
    }

    private void HandleSampleDocumentPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_isPointerSelecting &&
            (e.Property == SelectableTextBlock.SelectionStartProperty ||
                e.Property == SelectableTextBlock.SelectionEndProperty))
        {
            _hasToolbarPosition = false;
            UpdateToolbarPresentation();
        }
    }

    private void HandleStageLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateToolbarPosition();
    }

    private void UpdateToolbarPresentation()
    {
        if (_isPointerSelecting)
        {
            DemoToolbar.Classes.Set("visible", false);
            return;
        }

        var hasSelection = !string.IsNullOrWhiteSpace(SampleDocumentText.SelectedText);
        DemoToolbar.Classes.Set("visible", hasSelection);
        if (!hasSelection)
        {
            _hasToolbarPosition = false;
            DemoToolbar.CloseMenu();
            ClearActionPreview();
            return;
        }

        UpdateToolbarPosition();
    }

    private void UpdateToolbarPosition()
    {
        if (_isPointerSelecting || string.IsNullOrWhiteSpace(SampleDocumentText.SelectedText)) return;
        if (DemoStagePanel.Bounds.Width <= 1 || DemoToolbar.Bounds.Width <= 1 || DemoToolbar.Bounds.Height <= 1) return;

        if (TryGetSelectionBoundsInStage() is not { } targetRect) return;

        var toolbarWidth = DemoToolbar.Bounds.Width;
        var toolbarHeight = DemoToolbar.Bounds.Height;
        // TextSelectionToolbar carries a 10px shadow margin around its inner pill; overlap slightly so it sits snugly above the selection.
        var targetX = Math.Clamp(
            targetRect.Center.X - toolbarWidth * 0.5,
            -10d,
            Math.Max(-10d, DemoStagePanel.Bounds.Width - toolbarWidth + 10d));
        var targetY = targetRect.Top - toolbarHeight + 4d;

        if (!_hasToolbarPosition)
        {
            _hasToolbarPosition = true;
            _toolbarTransform.Transitions = null;
            _toolbarTransform.X = targetX;
            _toolbarTransform.Y = targetY;
            _toolbarTransform.Transitions = ToolbarPositionTransitions;
            return;
        }

        _toolbarTransform.X = targetX;
        _toolbarTransform.Y = targetY;
    }

    private Rect? TryGetSelectionBoundsInStage()
    {
        var start = Math.Min(SampleDocumentText.SelectionStart, SampleDocumentText.SelectionEnd);
        var length = Math.Abs(SampleDocumentText.SelectionEnd - SampleDocumentText.SelectionStart);
        if (length <= 0 || SampleDocumentText.Bounds.Width <= 1) return null;

        var hitRects = SampleDocumentText.TextLayout.HitTestTextRange(start, length);
        Rect? localSelection = null;
        foreach (var rect in hitRects)
        {
            localSelection = localSelection is { } current ? current.Union(rect) : rect;
        }

        if (localSelection is not { } selection) return null;
        if (SampleDocumentText.TranslatePoint(selection.TopLeft, DemoStagePanel) is not { } stageTopLeft) return null;

        return new Rect(stageTopLeft, selection.Size);
    }

    private void HandleToolbarActionInvoked(TextSelectionToolbarAction action)
    {
        var selectedText = SampleDocumentText.SelectedText;
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            return;
        }

        var compactSnippet = selectedText.Trim().ReplaceLineEndings(" ");
        if (compactSnippet.Length > MaxPreviewSnippetLength)
        {
            compactSnippet = $"{compactSnippet[..(MaxPreviewSnippetLength - 1)]}…";
        }

        PreviewActionIcon = action.DisplayIcon;
        PreviewActionHeaderKey = new FormattedDynamicLocaleKey(
            LocaleKey.Oobe_TextSelection_ActionPreviewFormat,
            action.NameKey,
            new DirectLocaleKey(compactSnippet));
        PreviewActionBodyKey = new FormattedDynamicLocaleKey(
            LocaleKey.Oobe_TextSelection_PreviewReplyFormat,
            action.NameKey);
        HasActionPreview = true;
    }

    private void HandleToolbarCloseRequested()
    {
        SampleDocumentText.ClearSelection();
        ClearActionPreview();
        UpdateToolbarPresentation();
    }

    private void ClearActionPreview()
    {
        HasActionPreview = false;
        PreviewActionIcon = null;
        PreviewActionHeaderKey = null;
        PreviewActionBodyKey = null;
    }

    private static IReadOnlyList<TextSelectionToolbarAction> CreateFallbackDemoActions() =>
    [
        new CustomTextSelectionToolbarAction
        {
            Name = LocaleKey.Strategy_BuiltIn_TextSelection_TranslateCommand_Name.I18N(),
            Icon = LucideIconKind.Languages,
            Prompt = "Translate"
        },
        new CustomTextSelectionToolbarAction
        {
            Name = LocaleKey.Strategy_BuiltIn_TextSelection_ExplainCommand_Name.I18N(),
            Icon = LucideIconKind.BookOpen,
            Prompt = "Explain"
        },
        new CustomTextSelectionToolbarAction
        {
            Name = LocaleKey.Strategy_BuiltIn_TextSelection_SummarizeCommand_Name.I18N(),
            Icon = LucideIconKind.FileText,
            Prompt = "Summarize"
        }
    ];
}
