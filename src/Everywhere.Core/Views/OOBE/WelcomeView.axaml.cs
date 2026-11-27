using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Everywhere.Views;

/// <summary>
/// Scene-agnostic onboarding host view providing ambient styling, header progress, scrollable
/// step decisions, an adaptive visual stage, selection energy stream, and celebration effects.
/// </summary>
public sealed partial class WelcomeView : UserControl
{
    /// <summary>
    /// Defines the <see cref="DisplayedStep"/> property.
    /// </summary>
    public static readonly StyledProperty<OobeStep?> DisplayedStepProperty =
        AvaloniaProperty.Register<WelcomeView, OobeStep?>(nameof(DisplayedStep));

    /// <summary>
    /// Defines the <see cref="IsSplitPresented"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsSplitPresentedProperty =
        AvaloniaProperty.Register<WelcomeView, bool>(nameof(IsSplitPresented));

    /// <summary>
    /// Defines the <see cref="IsConfigurePresented"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsConfigurePresentedProperty =
        AvaloniaProperty.Register<WelcomeView, bool>(nameof(IsConfigurePresented));

    /// <summary>
    /// Defines the <see cref="PresentedCompanionLayoutKey"/> property.
    /// </summary>
    public static readonly StyledProperty<string> PresentedCompanionLayoutKeyProperty =
        AvaloniaProperty.Register<WelcomeView, string>(nameof(PresentedCompanionLayoutKey), "hero");

    /// <summary>
    /// Gets the active onboarding flow hosted by this view.
    /// </summary>
    public OobeFlow Flow { get; }

    /// <summary>
    /// Gets the onboarding step currently presented by the decision surface and header.
    /// </summary>
    public OobeStep? DisplayedStep
    {
        get => GetValue(DisplayedStepProperty);
        private set => SetValue(DisplayedStepProperty, value);
    }

    /// <summary>
    /// Gets whether the workspace currently presents the split two-column layout.
    /// </summary>
    public bool IsSplitPresented
    {
        get => GetValue(IsSplitPresentedProperty);
        private set => SetValue(IsSplitPresentedProperty, value);
    }

    /// <summary>
    /// Gets whether the workspace currently presents the configuration step (whose card owns internal scrolling).
    /// </summary>
    public bool IsConfigurePresented
    {
        get => GetValue(IsConfigurePresentedProperty);
        private set => SetValue(IsConfigurePresentedProperty, value);
    }

    /// <summary>
    /// Gets the active companion layout key presented by <see cref="CompanionMotionPanel"/>.
    /// </summary>
    public string PresentedCompanionLayoutKey
    {
        get => GetValue(PresentedCompanionLayoutKeyProperty);
        private set => SetValue(PresentedCompanionLayoutKeyProperty, value);
    }

    private static readonly Transitions VerticalSlideTransitions =
    [
        new DoubleTransition
        {
            Property = TranslateTransform.YProperty,
            Duration = TimeSpan.FromMilliseconds(280),
            Easing = new CubicEaseOut()
        }
    ];

    private static readonly Transitions CrossLayoutFadeOutTransitions =
    [
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut()
        }
    ];

    private static readonly Transitions CrossLayoutFadeInTransitions =
    [
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut()
        }
    ];

    private bool _isAttached;
    private bool _hasLayoutAnchor;
    private bool _isCrossLayoutFadingOut;
    private int _streamRevealVersion;
    private int _layoutTransitionVersion;
    private double _lastHostArrangedY;
    private double _lastCompanionY;

    /// <summary>
    /// Initializes a new instance of the <see cref="WelcomeView"/> class.
    /// </summary>
    /// <param name="flow">The onboarding flow controlling this view.</param>
    public WelcomeView(OobeFlow flow)
    {
        Flow = flow;
        DataContext = flow;
        DisplayedStep = flow.CurrentStep;
        IsSplitPresented = flow.IsSplitLayout;
        IsConfigurePresented = flow.CurrentStep is WelcomeConfigurationStep;
        PresentedCompanionLayoutKey = flow.CompanionLayoutKey;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_isAttached) return;
        Flow.PropertyChanged += HandleFlowPropertyChanged;
        Flow.CelebrationRequested += HandleCelebrationRequested;
        LayoutUpdated += HandleLayoutUpdated;
        if (CompanionYHost.RenderTransform is TranslateTransform companion) companion.Changed += HandleStreamAnchorMoved;
        if (LeftDecisionHost.RenderTransform is TranslateTransform cards) cards.Changed += HandleStreamAnchorMoved;
        _isAttached = true;
        ResetCrossLayoutOpacityState(Flow.CurrentStep, Flow.IsSplitLayout, Flow.CompanionLayoutKey);
        UpdateVerticalSlideAndCompanionOffset();
        UpdateSelectionStream(isStepTransition: false);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_isAttached)
        {
            Flow.PropertyChanged -= HandleFlowPropertyChanged;
            Flow.CelebrationRequested -= HandleCelebrationRequested;
            LayoutUpdated -= HandleLayoutUpdated;
            if (CompanionYHost.RenderTransform is TranslateTransform companion) companion.Changed -= HandleStreamAnchorMoved;
            if (LeftDecisionHost.RenderTransform is TranslateTransform cards) cards.Changed -= HandleStreamAnchorMoved;
            _isAttached = false;
        }

        _hasLayoutAnchor = false;
        _isCrossLayoutFadingOut = false;
        _streamRevealVersion++;
        _layoutTransitionVersion++;
        SelectionStreamOverlay.IsHoldFadeIn = false;
        if (LeftDecisionHost.RenderTransform is TranslateTransform hostTransform)
        {
            hostTransform.Transitions = null;
            hostTransform.Y = 0d;
        }
        if (CompanionYHost.RenderTransform is TranslateTransform transform)
        {
            transform.Transitions = null;
        }

        CompanionMotionPanel.Transitions = null;
        StepHeaderPanel.Transitions = null;
        DecisionView.Transitions = null;
        RightStudioHost.Transitions = null;
        DecisionView.SetSlideSuppressed(false);

        ConfettiEffect?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void HandleFlowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OobeFlow.CurrentStep))
        {
            SyncPresentedStep();
            UpdateSelectionStream(isStepTransition: true);
            RefreshFooterButtonLayouts();
        }
        else if (e.PropertyName == nameof(WelcomeFlow.SelectedSource))
        {
            UpdateSelectionStream(isStepTransition: false);
        }
    }

    private void SyncPresentedStep()
    {
        var nextStep = Flow.CurrentStep;
        var nextSplit = Flow.IsSplitLayout;
        var nextConfigure = nextStep is WelcomeConfigurationStep;
        var nextCompanionKey = Flow.CompanionLayoutKey;

        if (nextSplit == IsSplitPresented && nextConfigure == IsConfigurePresented)
        {
            ResetCrossLayoutOpacityState(nextStep, nextSplit, nextCompanionKey);
            return;
        }

        // Cross-layout transitions (centered <-> split or centered <-> configure) fade the outgoing card,
        // header, right stage, and Evy orb out in place first, relocate cleanly at opacity 0, then fade back in together.
        var transitionVersion = ++_layoutTransitionVersion;
        _isCrossLayoutFadingOut = true;
        DecisionView.IsHitTestVisible = false;
        RightStudioHost.IsHitTestVisible = false;

        CompanionMotionPanel.Transitions = CrossLayoutFadeOutTransitions;
        StepHeaderPanel.Transitions = CrossLayoutFadeOutTransitions;
        DecisionView.Transitions = CrossLayoutFadeOutTransitions;
        CompanionMotionPanel.Opacity = 0d;
        StepHeaderPanel.Opacity = 0d;
        DecisionView.Opacity = 0d;

        if (IsSplitPresented)
        {
            RightStudioHost.Transitions = CrossLayoutFadeOutTransitions;
            RightStudioHost.Opacity = 0d;
        }

        DispatcherTimer.RunOnce(
            () =>
            {
                if (!_isAttached || transitionVersion != _layoutTransitionVersion) return;

                _isCrossLayoutFadingOut = false;
                DecisionView.SetSlideSuppressed(true);
                _hasLayoutAnchor = false;
                _isCrossLayoutFadingOut = false;
                if (LeftDecisionHost.RenderTransform is TranslateTransform hostTransform)
                {
                    hostTransform.Transitions = null;
                    hostTransform.Y = 0d;
                }
                if (CompanionYHost.RenderTransform is TranslateTransform companionTransform)
                {
                    companionTransform.Transitions = null;
                }

                RightStudioHost.Transitions = null;
                RightStudioHost.Opacity = 0d;
                IsSplitPresented = nextSplit;
                IsConfigurePresented = nextStep is WelcomeConfigurationStep;
                PresentedCompanionLayoutKey = nextCompanionKey;
                DisplayedStep = nextStep;

                DispatcherTimer.RunOnce(
                    () =>
                    {
                        if (!_isAttached || transitionVersion != _layoutTransitionVersion) return;

                        UpdateVerticalSlideAndCompanionOffset();

                        CompanionMotionPanel.Transitions = CrossLayoutFadeInTransitions;
                        StepHeaderPanel.Transitions = CrossLayoutFadeInTransitions;
                        DecisionView.Transitions = CrossLayoutFadeInTransitions;
                        CompanionMotionPanel.Opacity = 1d;
                        StepHeaderPanel.Opacity = 1d;
                        DecisionView.Opacity = 1d;
                        DecisionView.IsHitTestVisible = true;

                        if (nextSplit)
                        {
                            RightStudioHost.Transitions = CrossLayoutFadeInTransitions;
                            RightStudioHost.Opacity = 1d;
                            RightStudioHost.IsHitTestVisible = true;
                        }
                        else
                        {
                            RightStudioHost.Opacity = 1d;
                            RightStudioHost.IsHitTestVisible = false;
                        }

                        Dispatcher.UIThread.Post(
                            () =>
                            {
                                if (_isAttached && transitionVersion == _layoutTransitionVersion)
                                {
                                    DecisionView.SetSlideSuppressed(false);
                                }
                            },
                            DispatcherPriority.Background);
                    },
                    TimeSpan.FromMilliseconds(45));
            },
            TimeSpan.FromMilliseconds(160));
    }

    private void ResetCrossLayoutOpacityState(OobeStep? step, bool isSplit, string companionKey)
    {
        _layoutTransitionVersion++;
        _isCrossLayoutFadingOut = false;
        DecisionView.SetSlideSuppressed(false);
        CompanionMotionPanel.Transitions = null;
        StepHeaderPanel.Transitions = null;
        DecisionView.Transitions = null;
        RightStudioHost.Transitions = null;
        CompanionMotionPanel.Opacity = 1d;
        StepHeaderPanel.Opacity = 1d;
        DecisionView.Opacity = 1d;
        RightStudioHost.Opacity = 1d;
        DecisionView.IsHitTestVisible = true;
        RightStudioHost.IsHitTestVisible = isSplit;
        IsSplitPresented = isSplit;
        IsConfigurePresented = step is WelcomeConfigurationStep;
        PresentedCompanionLayoutKey = companionKey;
        DisplayedStep = step;
    }

    private void HandleCelebrationRequested(object? sender, EventArgs e)
    {
        ConfettiEffect?.Start();
    }

    private void HandleLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateVerticalSlideAndCompanionOffset();
    }

    // A FLIP transition moves the anchors without causing a layout pass.
    private void HandleStreamAnchorMoved(object? sender, EventArgs e) => SelectionStreamOverlay.UpdateGeometry();

    private void UpdateVerticalSlideAndCompanionOffset()
    {
        if (_isCrossLayoutFadingOut) return;
        if (CompanionMotionPanel.Bounds.Height <= 1 || LeftDecisionHost.Bounds.Height <= 1) return;
        if (CompanionYHost.RenderTransform is not TranslateTransform companionTransform) return;
        if (LeftDecisionHost.RenderTransform is not TranslateTransform hostTransform) return;

        var arrangedHostY = LeftDecisionHost.Bounds.Y;

        if (!_hasLayoutAnchor)
        {
            _hasLayoutAnchor = true;
            _lastHostArrangedY = arrangedHostY;
            hostTransform.Transitions = null;
            hostTransform.Y = 0d;
            hostTransform.Transitions = VerticalSlideTransitions;

            var initialCompanionY = TryResolveArrangedCompanionY() ?? 0d;
            _lastCompanionY = initialCompanionY;
            companionTransform.Transitions = null;
            companionTransform.Y = initialCompanionY;
            companionTransform.Transitions = VerticalSlideTransitions;
            return;
        }

        // Smoothly glide LeftDecisionHost (header + step content) via FLIP when layout centering shifts vertically.
        // In split or configure mode, expanding the card must not temporarily translate DecisionScrollViewer downward over FooterPanel.
        var hostDeltaY = _lastHostArrangedY - arrangedHostY;
        if (IsSplitPresented || IsConfigurePresented)
        {
            _lastHostArrangedY = arrangedHostY;
            if (Math.Abs(hostTransform.Y) > 0.5)
            {
                hostTransform.Transitions = null;
                hostTransform.Y = 0d;
                hostTransform.Transitions = VerticalSlideTransitions;
            }
        }
        else if (Math.Abs(hostDeltaY) > 0.5)
        {
            _lastHostArrangedY = arrangedHostY;
            var startOffset = hostTransform.Y + hostDeltaY;
            hostTransform.Transitions = null;
            hostTransform.Y = startOffset;
            hostTransform.Transitions = VerticalSlideTransitions;
            hostTransform.Y = 0d;
        }

        if (TryResolveArrangedCompanionY() is not { } targetY) return;
        if (Math.Abs(_lastCompanionY - targetY) <= 0.5) return;
        _lastCompanionY = targetY;
        companionTransform.Y = targetY;
    }

    private double? TryResolveArrangedCompanionY()
    {
        if (IsSplitPresented)
        {
            var splitAnchor = RightStudioHost.FindDescendantOfType<WelcomeStageView>()?.FindControl<Border>("SplitCompanionAnchorSpacer");
            if (splitAnchor is not { IsEffectivelyVisible: true } || splitAnchor.Bounds.Height <= 0) return null;
            return splitAnchor.TranslatePoint(default, CompanionMotionPanel) is { } origin ? Math.Max(0d, origin.Y) : null;
        }

        // Compute pure arranged Y inside BodyGrid row 0 (unaffected by LeftDecisionHost's active FLIP TranslateTransform)
        // so Evy and the header text share the identical 280ms curve and destination without compounding lag.
        if (CompanionAnchorSpacer is not { IsEffectivelyVisible: true } || CompanionAnchorSpacer.Bounds.Height <= 0)
        {
            return null;
        }

        return Math.Max(0d, LeftDecisionHost.Bounds.Y + CompanionAnchorSpacer.Bounds.Y);
    }

    private void UpdateSelectionStream(bool isStepTransition)
    {
        if (Flow is WelcomeFlow { CurrentStep: WelcomeSourceStep, SelectedSource: var source })
        {
            if (isStepTransition)
            {
                var revealVersion = ++_streamRevealVersion;
                SelectionStreamOverlay.IsHoldFadeIn = true;
                DispatcherTimer.RunOnce(
                    () =>
                    {
                        if (!_isAttached || revealVersion != _streamRevealVersion) return;
                        SelectionStreamOverlay.IsHoldFadeIn = false;
                    },
                    TimeSpan.FromMilliseconds(235));
            }

            SelectionStreamOverlay.TargetKey = source switch
            {
                WelcomeModelSource.Cloud => "SourceCloudCard",
                WelcomeModelSource.Own => "SourceOwnCard",
                _ => null
            };
            return;
        }

        _streamRevealVersion++;
        SelectionStreamOverlay.IsHoldFadeIn = false;
        SelectionStreamOverlay.TargetKey = null;
    }

    private void RefreshFooterButtonLayouts()
    {
        RefreshFooterButtonLayout(PrimaryActionButton);
        RefreshFooterButtonLayout(SecondaryActionButton);
    }

    private static void RefreshFooterButtonLayout(Button button)
    {
        if (button.Content is Control content)
        {
            content.InvalidateMeasure();
            content.InvalidateArrange();
        }

        if (button.FindDescendantOfType<ShadUI.ButtonContentPresenter>() is { } container)
        {
            container.InvalidateMeasure();
            container.InvalidateArrange();
        }
    }
}
