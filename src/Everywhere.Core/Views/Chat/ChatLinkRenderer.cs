using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Everywhere.Chat.Links;
using Everywhere.Utilities;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;

namespace Everywhere.Views;

/// <summary>
/// Opts chat Markdown into file actions and previews. Other Markdown surfaces keep their own link policy.
/// </summary>
public class ChatLinkRenderer : MarkdownRenderer
{
    protected override Type StyleKeyOverride => typeof(MarkdownRenderer);

    private readonly Dictionary<Link, LinkDecoration> _decorations = new();

    private MarkdownTextBlock? _hoverBlock;
    private Link? _hoverLink;
    private Point _pointerPosition;
    private bool _isPointerDown;
    private IDisposable? _hoverDelay;
    private ContextMenu? _menu;
    private IDisposable? _menuValue;
    private Popup? _popup;
    private ChatWebLinkCard? _card;
    private CancellationTokenSource? _previewCancellation;
    private TopLevel? _topLevel;
    private ScrollViewer? _scrollViewer;
    private bool _isPreviewDismissed;

    public ChatLinkRenderer()
    {
        // Register before a detached renderer can receive its first DocumentUpdate.
        // The renderer owns this scope; node release removes per-link resources.
        NodeHooks.Register<LinkInlineNode>(
            beforeUpdate: context =>
            {
                if (context.Node.Inline is Link link && _decorations.TryGetValue(link, out var decoration))
                {
                    link.Inlines.Remove(decoration.Inline);
                }
            },
            afterUpdate: context =>
            {
                if (context.Node.Inline is not Link { Image: null, HRef: { } href } link ||
                    context.Result != NodeUpdateResult.Applied)
                {
                    return;
                }

                var target = ChatLinkTarget.Parse(href.OriginalString);
                if (target.Kind == ChatLinkKind.Unsupported)
                {
                    if (_decorations.Remove(link, out var oldDecoration)) oldDecoration.Dispose();
                    return;
                }

                if (!_decorations.TryGetValue(link, out var decoration))
                {
                    decoration = new LinkDecoration();
                    _decorations.Add(link, decoration);
                }

                if (decoration.Target != target)
                {
                    decoration.Reset(target);
                }

                link.Inlines.Insert(0, decoration.Inline);
            },
            cleanup: context =>
            {
                if (context.Node.Inline is not Link link) return;
                if (ReferenceEquals(link, _hoverLink)) ResetInteraction();
                if (_decorations.Remove(link, out var decoration))
                {
                    link.Inlines.Remove(decoration.Inline);
                    decoration.Dispose();
                }
            });

        LinkClick += HandleLinkClick;
        LinkPointerOverChanged += HandleLinkHoverChanged;
        AddHandler(PointerMovedEvent, HandlePointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, HandlePointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _menuValue = SetValue(LinkContextMenuProperty, null, BindingPriority.StyleTrigger);
        _topLevel?.PropertyChanged += HandleTopLevelChanged;
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        _scrollViewer?.ScrollChanged += HandleScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ResetInteraction();

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= HandleScrollChanged;
            _scrollViewer = null;
        }

        if (_topLevel is not null)
        {
            _topLevel.PropertyChanged -= HandleTopLevelChanged;
            _topLevel = null;
        }

        DisposeHelper.DisposeToDefault(ref _menuValue);
        _menu = null;

        foreach (var decoration in _decorations.Values)
        {
            decoration.ClearFavicon();
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentUpdateProperty && change.NewValue is null ||
            change.Property == IsVisibleProperty && !IsVisible ||
            change.Property == IsEnabledProperty && !IsEnabled)
        {
            ResetInteraction();
        }
    }

    private void HandlePointerMoved(object? sender, PointerEventArgs e)
    {
        _pointerPosition = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        _isPointerDown = properties.IsLeftButtonPressed || properties.IsRightButtonPressed || properties.IsMiddleButtonPressed;
        if (_hoverLink is null && e.Source is MarkdownTextBlock { PointerOverLink: not null } block)
        {
            SetHoverBlock(block);
        }
        if (_popup is null)
        {
            StartHoverTimer();
        }
    }

    private void HandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isPointerDown = true;
        ClosePreview();
    }

    private void HandleLinkClick(object? sender, LinkClickedEventArgs e)
    {
        if (e.HRef is not { } address) return;

        e.Handled = true;
        ClosePreview();
        if (_topLevel is not { } owner || owner.GetValue(ChatLinkInteraction.InteractionProperty) is not { } interaction) return;

        var target = ChatLinkTarget.Parse(address.OriginalString);
        interaction.ExecuteAsync(owner, target, target.Kind == ChatLinkKind.Local ? ChatLinkAction.FollowLocalLink : ChatLinkAction.Open).Detach();
    }

    private void HandleLinkHoverChanged(object? sender, RoutedEventArgs e)
    {
        if (_topLevel is null || e.Source is not MarkdownTextBlock block) return;
        SetHoverBlock(block);
    }

    private void SetHoverBlock(MarkdownTextBlock block)
    {
        if (block.PointerOverLink is not { } link)
        {
            if (!ReferenceEquals(block, _hoverBlock)) return;
            ResetHover();
            return;
        }

        if (ReferenceEquals(link, _hoverLink))
        {
            StartHoverTimer();
            return;
        }

        ClosePreview();
        _isPreviewDismissed = false;
        _hoverBlock = block;
        ObserveLink(link);
        EnsureMenu();
        StartHoverTimer();
    }

    private void EnsureMenu()
    {
        if (_menu is not null || _topLevel is not { } owner ||
            owner.GetValue(ChatLinkInteraction.InteractionProperty) is not { } interaction)
        {
            return;
        }

        _menu = ChatLinkMenu.Create(owner, interaction);
        _menu.DataContextChanged += (_, _) => ClosePreview();
        _menuValue?.Dispose();
        _menuValue = SetValue(LinkContextMenuProperty, _menu, BindingPriority.StyleTrigger);
    }

    private void StartHoverTimer()
    {
        if (_isPreviewDismissed || _popup is not null || _hoverDelay is not null || _isPointerDown || _menu?.IsOpen == true ||
            _hoverBlock is not { } block || !ReferenceEquals(block.PointerOverLink, _hoverLink) ||
            _hoverLink?.HRef is not { } address)
        {
            return;
        }

        var delay = TimeSpan.FromMilliseconds(ChatLinkTarget.Parse(address.OriginalString).Kind == ChatLinkKind.Web ? 300 : 500);
        _hoverDelay = DispatcherTimer.RunOnce(ShowHoveredLink, delay, DispatcherPriority.Default);
    }

    private void ShowHoveredLink()
    {
        DisposeHelper.DisposeToDefault(ref _hoverDelay);

        if (_isPointerDown ||
            _menu?.IsOpen == true ||
            _hoverBlock is not { } block ||
            _hoverLink is not { } link ||
            !ReferenceEquals(block.PointerOverLink, link))
        {
            return;
        }

        ShowPopup(block, link);
    }

    private void ShowPopup(MarkdownTextBlock block, Link link)
    {
        if (_topLevel is not { IsVisible: true } topLevel ||
            !IsEffectivelyVisible ||
            !IsEnabled ||
            link.HRef is not { } address ||
            block.GetInlineGeometry(link) is not { Rects.Count: > 0 } geometry)
        {
            return;
        }

        var target = ChatLinkTarget.Parse(address.OriginalString);
        if (target.Kind == ChatLinkKind.Unsupported) return;

        var anchor = geometry.Rects[0];
        if (this.TranslatePoint(_pointerPosition, block) is { } point)
        {
            foreach (var rect in geometry.Rects)
            {
                if (!rect.Contains(point)) continue;
                anchor = rect;
                break;
            }
        }

        var placement = PlacementMode.BottomEdgeAlignedLeft;
        if (target.Kind == ChatLinkKind.Web &&
            block.TranslatePoint(anchor.BottomLeft, topLevel) is { } bottom &&
            topLevel.ClientSize.Height - bottom.Y < 280 &&
            bottom.Y > topLevel.ClientSize.Height / 2)
        {
            placement = PlacementMode.TopEdgeAlignedLeft;
        }

        var popup = new Popup
        {
            ShouldUseOverlayLayer = true,
            IsHitTestVisible = false,
            PlacementTarget = block,
            PlacementRect = anchor,
            Placement = placement,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY
        };
        ((ISetLogicalParent)popup).SetParent(topLevel);
        _popup = popup;

        if (target.Kind == ChatLinkKind.Web)
        {
            ShowWebCard(popup, topLevel, link, target);
        }
        else
        {
            var tooltip = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Child = new TextBlock { Text = target.Address, MaxWidth = 450, TextWrapping = TextWrapping.Wrap },
                [!Border.BackgroundProperty] = new DynamicResourceExtension("PopupBackgroundColor"),
            };
            popup.Child = tooltip;
        }
        topLevel.AddHandler(KeyDownEvent, HandlePreviewKey, RoutingStrategies.Tunnel);
        popup.IsOpen = true;
    }

    private void ShowWebCard(Popup popup, TopLevel owner, Link link, ChatLinkTarget target)
    {
        var address = new Uri(target.Address);
        var card = _card = new ChatWebLinkCard();
        card.SetAddress(address, owner.ClientSize.Width);
        popup.Child = card;

        if (owner.GetValue(ChatLinkInteraction.InteractionProperty) is not { IsPreviewEnabled: true } interaction ||
            !ChatLinkInteraction.IsPreviewable(address))
        {
            return;
        }

        if (_decorations.TryGetValue(link, out var decoration) && decoration.Favicon is { } icon)
        {
            card.SetIcon(icon);
        }

        LoadPreviewAsync(card, link, interaction, address).Detach();
    }

    private async Task LoadPreviewAsync(ChatWebLinkCard card, Link link, ChatLinkInteraction interaction, Uri address)
    {
        if (_previewCancellation is not null) return;

        using var wait = new CancellationTokenSource();
        var token = wait.Token;
        _previewCancellation = wait;
        try
        {
            var progress = new Progress<WebPreview>(ApplyIfCurrent);
            var result = await interaction.GetPreviewAsync(address, token, progress);
            if (result is not null) ApplyIfCurrent(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, wait)) _previewCancellation = null;
        }

        void ApplyIfCurrent(WebPreview preview)
        {
            if (ReferenceEquals(_card, card) && !token.IsCancellationRequested && interaction.IsPreviewEnabled)
            {
                ApplyPreview(card, link, preview);
            }
        }
    }

    private void ApplyPreview(ChatWebLinkCard card, Link link, WebPreview preview)
    {
        card.Show(preview);
        if (preview.Icon is { } bytes && _decorations.TryGetValue(link, out var decoration))
        {
            decoration.SetFavicon(bytes);
        }
    }

    private void HandleScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta != default) ResetInteraction();
    }

    private void HandlePreviewKey(object? sender, KeyEventArgs e)
    {
        if (_popup is null) return;
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _isPreviewDismissed = true;
        ClosePreview();
    }

    private void HandleTopLevelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && _topLevel is { IsVisible: false }) ResetInteraction();
    }

    private void ObserveLink(Link? link)
    {
        if (_hoverLink is not null) _hoverLink.PropertyChanged -= HandleLinkChanged;
        _hoverLink = link;
        if (link is not null) link.PropertyChanged += HandleLinkChanged;
    }

    private void HandleLinkChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Link.HRefProperty) ResetInteraction();
    }

    private void ResetInteraction()
    {
        ResetHover();
        _menu?.Close();
        _isPointerDown = false;
    }

    private void ResetHover()
    {
        ClosePreview();
        _hoverBlock = null;
        _isPreviewDismissed = false;
        ObserveLink(null);
    }

    private void ClosePreview()
    {
        DisposeHelper.DisposeToDefault(ref _hoverDelay);
        DisposeHelper.CancelAndDisposeToDefault(ref _previewCancellation);

        if (_popup is { } popup)
        {
            _topLevel?.RemoveHandler(KeyDownEvent, HandlePreviewKey);
            _popup = null;
            popup.IsOpen = false;
            popup.Child = null;
            popup.PlacementTarget = null;
            ((ISetLogicalParent)popup).SetParent(null);
        }

        DisposeHelper.DisposeToDefault(ref _card);
    }

    private sealed class LinkDecoration : Panel, IDisposable
    {
        public InlineUIContainer Inline { get; }
        public ChatLinkTarget? Target { get; private set; }
        public byte[]? Favicon { get; private set; }

        private readonly LucideIcon _placeholder = new()
        {
            [!LucideIcon.SizeProperty] = new DynamicResourceExtension("FontSizeL")
        };

        private Image? _image;
        private Bitmap? _bitmap;
        private Uri? _sitePage;
        private IDisposable? _iconDelay;
        private CancellationTokenSource? _iconRequest;
        private bool _hasResolvedIcon;
        private bool _hasCheckedDirectory;

        public LinkDecoration()
        {
            this[!WidthProperty] = this[!HeightProperty] = new DynamicResourceExtension("FontSizeM");
            Margin = new Thickness(0, 0, 4, 0);
            IsHitTestVisible = false;
            Inline = new InlineUIContainer(this);
            Children.Add(_placeholder);
        }

        public void Reset(ChatLinkTarget target)
        {
            StopIconLookup();
            ClearFavicon();
            EffectiveViewportChanged -= HandleIconViewportChanged;
            Target = target;
            _hasCheckedDirectory = false;

            var page = target.Kind == ChatLinkKind.Web ? new Uri(target.Address) : null;
            _sitePage = page is not null && ChatLinkInteraction.IsPreviewable(page) ? page : null;
            _placeholder.Kind = target.Kind == ChatLinkKind.Web ? LucideIconKind.Globe : ChatFileIcons.FromPath(target.Address);
            if (_sitePage is not null || target.Kind == ChatLinkKind.Local && _placeholder.Kind != LucideIconKind.Folder)
            {
                EffectiveViewportChanged += HandleIconViewportChanged;
            }
        }

        private void HandleIconViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
        {
            var visible = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
            if (!IsEffectivelyVisible || visible.Width <= 0 || visible.Height <= 0)
            {
                StopIconLookup();
                return;
            }

            if (_hasResolvedIcon || _hasCheckedDirectory || Favicon is not null || _iconDelay is not null || _iconRequest is not null) return;
            _iconDelay = DispatcherTimer.RunOnce(
                () =>
                {
                    _iconDelay = null;
                    if (Target is not { } target || !IsEffectivelyVisible ||
                        TopLevel.GetTopLevel(this) is not { IsVisible: true } owner)
                    {
                        return;
                    }

                    if (target.Kind == ChatLinkKind.Local ||
                        owner.GetValue(ChatLinkInteraction.InteractionProperty) is { IsPreviewEnabled: true })
                    {
                        ResolveIconAsync(target, owner).Detach();
                    }
                },
                TimeSpan.FromMilliseconds(120),
                DispatcherPriority.Background);
        }

        private async Task ResolveIconAsync(ChatLinkTarget target, TopLevel owner)
        {
            using var request = new CancellationTokenSource();
            _iconRequest = request;
            try
            {
                if (target.Kind == ChatLinkKind.Local)
                {
                    var isDirectory = await ChatFileIcons.ProbeDirectoryAsync(target.Address, request.Token);
                    if (request.IsCancellationRequested) return;

                    _hasCheckedDirectory = true;
                    if (isDirectory) _placeholder.Kind = LucideIconKind.Folder;
                    return;
                }

                if (_sitePage is not { } page ||
                    owner.GetValue(ChatLinkInteraction.InteractionProperty) is not { IsPreviewEnabled: true } interaction) return;
                var bytes = await interaction.GetSiteIconAsync(page, request.Token);
                if (request.IsCancellationRequested || !interaction.IsPreviewEnabled) return;

                _hasResolvedIcon = true;
                // A page-declared icon found on hover takes precedence over a late root favicon.
                if (Favicon is null && bytes is not null) SetFavicon(bytes);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_iconRequest, request)) _iconRequest = null;
            }
        }

        private void StopIconLookup()
        {
            DisposeHelper.DisposeToDefault(ref _iconDelay);
            var request = _iconRequest;
            _iconRequest = null;
            request?.Cancel();
            _hasResolvedIcon = false;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            // LiveMarkdown temporarily removes the inline during an in-place streaming update.
            if (_iconDelay is not null || _iconRequest is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!this.IsAttachedToVisualTree()) StopIconLookup();
                });
            }
        }

        public void SetFavicon(byte[] bytes)
        {
            if (ReferenceEquals(bytes, Favicon)) return;

            ClearFavicon();
            using var stream = new MemoryStream(bytes, writable: false);
            _bitmap = new Bitmap(stream);
            Favicon = bytes;
            _image = new Image { Source = _bitmap };
            Children.Add(_image);
            _placeholder.IsVisible = false;
        }

        public void ClearFavicon()
        {
            if (_image is not null) Children.Remove(_image);
            _image = null;
            DisposeHelper.DisposeToDefault(ref _bitmap);
            Favicon = null;
            _placeholder.IsVisible = true;
        }

        public void Dispose()
        {
            StopIconLookup();
            EffectiveViewportChanged -= HandleIconViewportChanged;
            ClearFavicon();
        }
    }
}