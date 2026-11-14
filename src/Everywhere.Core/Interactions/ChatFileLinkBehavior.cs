using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Xaml.Interactivity;
using Everywhere.Chat.Links;
using Everywhere.Chat.Plugins;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.Interactions;

/// <summary>
/// Gives a realized tool file reference the same actions as a Markdown file link.
/// </summary>
public sealed class ChatFileLinkBehavior : Behavior<Button>
{
    public static readonly StyledProperty<ChatPluginFileReference?> ReferenceProperty =
        AvaloniaProperty.Register<ChatFileLinkBehavior, ChatPluginFileReference?>(nameof(Reference));

    public ChatPluginFileReference? Reference
    {
        get => GetValue(ReferenceProperty);
        set => SetValue(ReferenceProperty, value);
    }

    public static readonly StyledProperty<LucideIcon?> IconProperty =
        AvaloniaProperty.Register<ChatFileLinkBehavior, LucideIcon?>(nameof(Icon));

    /// <summary>
    /// The icon displayed for this reference, updated when a visible path is identified as a directory.
    /// </summary>
    public LucideIcon? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private ContextMenu? _menu;
    private CancellationTokenSource? _iconCheck;
    private bool _hasCheckedIcon;
    private bool _isInViewport;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is not { } button) return;

        button.Click += HandleClick;
        button.ContextRequested += HandleContextRequested;
        button.DetachedFromVisualTree += HandleDetached;
        button.EffectiveViewportChanged += HandleViewportChanged;
        ResetIcon();
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is { } button)
        {
            button.Click -= HandleClick;
            button.ContextRequested -= HandleContextRequested;
            button.DetachedFromVisualTree -= HandleDetached;
            button.EffectiveViewportChanged -= HandleViewportChanged;
        }

        CloseMenu();
        ResetIcon();
        _isInViewport = false;
        base.OnDetaching();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ReferenceProperty || change.Property == IsEnabledProperty) CloseMenu();
        if (change.Property == ReferenceProperty || change.Property == IconProperty || change.Property == IsEnabledProperty)
        {
            ResetIcon();
            UpdateVisibleIcon();
        }
    }

    private void HandleClick(object? sender, RoutedEventArgs e)
    {
        if (!IsEnabled || sender is not Button button || Reference is not { } reference) return;

        e.Handled = true;
        if (TopLevel.GetTopLevel(button) is { } owner && owner.GetValue(ChatLinkInteraction.InteractionProperty) is { } interaction)
        {
            interaction.ExecuteAsync(owner, ChatLinkTarget.Parse(reference.FullPath), ChatLinkAction.FollowLocalLink).Detach();
        }
    }

    private void HandleContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!IsEnabled || sender is not Button button || Reference is not { } reference) return;
        if (TopLevel.GetTopLevel(button) is not { } owner || owner.GetValue(ChatLinkInteraction.InteractionProperty) is not { } interaction) return;

        e.Handled = true;
        var menu = _menu ??= ChatLinkMenu.Create(owner, interaction);
        menu.DataContext = ChatLinkTarget.Parse(reference.FullPath);
        menu.Open(button);
    }

    private void HandleDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        CloseMenu();
        ResetIcon();
        _isInViewport = false;
    }

    private void HandleViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (sender is not Button button) return;
        var visible = e.EffectiveViewport.Intersect(new Rect(button.Bounds.Size));
        _isInViewport = visible.Width > 0 && visible.Height > 0;
        if (_isInViewport)
        {
            UpdateVisibleIcon();
        }
        else if (_iconCheck is { } check)
        {
            _iconCheck = null;
            _hasCheckedIcon = false;
            check.Cancel();
        }
    }

    private void UpdateVisibleIcon()
    {
        if (!IsEnabled || !_isInViewport || _hasCheckedIcon || Icon is not { } icon || Reference is not { } reference ||
            reference.Icon == LucideIconKind.Folder || AssociatedObject is not { IsEffectivelyVisible: true } button ||
            TopLevel.GetTopLevel(button) is not { IsVisible: true })
        {
            return;
        }

        if (ChatLinkTarget.Parse(reference.FullPath) is { Kind: ChatLinkKind.Local } target)
        {
            _hasCheckedIcon = true;
            UpdateIconAsync(icon, target.Address).Detach();
        }
    }

    private async Task UpdateIconAsync(LucideIcon icon, string path)
    {
        using var check = new CancellationTokenSource();
        _iconCheck = check;
        try
        {
            if (await ChatFileIcons.ProbeDirectoryAsync(path, check.Token) && !check.IsCancellationRequested)
            {
                icon.Kind = LucideIconKind.Folder;
            }
        }
        finally
        {
            if (ReferenceEquals(_iconCheck, check)) _iconCheck = null;
        }
    }

    private void ResetIcon()
    {
        _iconCheck?.Cancel();
        _iconCheck = null;
        _hasCheckedIcon = false;
        if (Icon is { } icon) icon.Kind = Reference?.Icon ?? LucideIconKind.File;
    }

    private void CloseMenu()
    {
        _menu?.Close();
        _menu = null;
    }
}