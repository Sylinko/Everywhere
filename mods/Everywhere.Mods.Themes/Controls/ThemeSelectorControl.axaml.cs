using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Everywhere.I18N;

namespace Everywhere.Mods.Themes.Controls;

/// <summary>
/// Presents a selectable theme dropdown using compact Codex-style <c>Aa</c> swatch badges.
/// </summary>
public sealed partial class ThemeSelectorControl : UserControl
{
    /// <summary>
    /// Identifies the <see cref="Variant"/> property.
    /// </summary>
    public static readonly StyledProperty<ThemeBaseVariant> VariantProperty =
        AvaloniaProperty.Register<ThemeSelectorControl, ThemeBaseVariant>(nameof(Variant));

    /// <summary>
    /// Identifies the <see cref="SelectedThemeId"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> SelectedThemeIdProperty =
        AvaloniaProperty.Register<ThemeSelectorControl, string?>(
            nameof(SelectedThemeId),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// Gets or sets whether this selector lists light or dark themes.
    /// </summary>
    public ThemeBaseVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    /// <summary>
    /// Gets or sets the selected theme identifier.
    /// </summary>
    public string? SelectedThemeId
    {
        get => GetValue(SelectedThemeIdProperty);
        set => SetValue(SelectedThemeIdProperty, value);
    }

    private readonly ComboBox _comboBox;
    private IReadOnlyList<ThemeOption> _options = [];
    private bool _isAttached;
    private bool _isSyncingSelection;

    /// <summary>
    /// Initializes a new <see cref="ThemeSelectorControl"/> instance.
    /// </summary>
    public ThemeSelectorControl()
    {
        AvaloniaXamlLoader.Load(this);
        _comboBox = this.FindControl<ComboBox>("ThemeComboBox")
            ?? throw new InvalidOperationException("ThemeComboBox part was not found.");
        _comboBox.SelectionChanged += HandleComboBoxSelectionChanged;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _isAttached = true;
        ThemeRegistry.Changed += HandleThemeRegistryChanged;
        RefreshOptions();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeRegistry.Changed -= HandleThemeRegistryChanged;
        _isAttached = false;

        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (!_isAttached)
        {
            return;
        }

        if (change.Property == VariantProperty)
        {
            RefreshOptions();
        }
        else if (change.Property == SelectedThemeIdProperty && !_isSyncingSelection)
        {
            SyncComboBoxSelection();
        }
    }

    private void HandleThemeRegistryChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (_isAttached)
            {
                RefreshOptions();
            }

            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isAttached)
            {
                RefreshOptions();
            }
        });
    }

    private void RefreshOptions()
    {
        var themes = ThemeRegistry.GetThemes(Variant);
        _options =
        [
            .. themes.Select(static theme => new ThemeOption(
                theme.Id,
                theme.ResolvedDisplayNameKey,
                new ImmutableSolidColorBrush(theme.Palette.WindowBackground),
                new ImmutableSolidColorBrush(theme.Palette.Primary),
                new ImmutableSolidColorBrush(theme.Palette.Border)))
        ];

        _isSyncingSelection = true;
        try
        {
            _comboBox.ItemsSource = _options;
            SyncComboBoxSelection();
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void SyncComboBoxSelection()
    {
        var targetId = SelectedThemeId;
        var match = _options.FirstOrDefault(option => string.Equals(option.Id, targetId, StringComparison.OrdinalIgnoreCase))
            ?? _options.FirstOrDefault();

        _isSyncingSelection = true;
        try
        {
            _comboBox.SelectedItem = match;
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void HandleComboBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSelection || _comboBox.SelectedItem is not ThemeOption option)
        {
            return;
        }

        _isSyncingSelection = true;
        try
        {
            SelectedThemeId = option.Id;
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }
}

/// <summary>
/// Lightweight presentation item rendered inside <see cref="ThemeSelectorControl"/>.
/// </summary>
internal sealed record ThemeOption(
    string Id,
    IDynamicLocaleKey DisplayNameKey,
    IImmutableBrush BadgeBackground,
    IImmutableBrush BadgeForeground,
    IImmutableBrush BadgeBorder);
