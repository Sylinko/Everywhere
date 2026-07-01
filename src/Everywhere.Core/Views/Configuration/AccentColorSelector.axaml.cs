using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Everywhere.Views;

public sealed class AccentColorSelector : TemplatedControl
{
    public static Color[] PresetColors { get; } =
    [
        new(0xFF, 0xEC, 0xD4, 0x52),
        new(0xFF, 0xD2, 0x39, 0x18),
        new(0xFF, 0x77, 0x96, 0x49),
        new(0xFF, 0x00, 0x71, 0x75),
        new(0xFF, 0x32, 0x71, 0xAE),
        new(0xFF, 0xA6, 0x55, 0x9D),
    ];

    public static readonly StyledProperty<Color?> SelectedColorProperty =
        AvaloniaProperty.Register<AccentColorSelector, Color?>(nameof(SelectedColor));

    public Color? SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public static readonly DirectProperty<AccentColorSelector, bool> UseSystemAccentColorProperty =
        AvaloniaProperty.RegisterDirect<AccentColorSelector, bool>(
            nameof(UseSystemAccentColor),
            o => o.UseSystemAccentColor,
            (o, v) => o.UseSystemAccentColor = v);

    public bool UseSystemAccentColor
    {
        get;
        set => SetAndRaise(UseSystemAccentColorProperty, ref field, value);
    } = true;

    public static readonly DirectProperty<AccentColorSelector, Color?> SelectedPresetColorProperty =
        AvaloniaProperty.RegisterDirect<AccentColorSelector, Color?>(
            nameof(SelectedPresetColor),
            o => o.SelectedPresetColor,
            (o, v) => o.SelectedPresetColor = v);

    public Color? SelectedPresetColor
    {
        get;
        set => SetAndRaise(SelectedPresetColorProperty, ref field, value);
    }

    public static readonly DirectProperty<AccentColorSelector, bool> UseCustomAccentColorProperty =
        AvaloniaProperty.RegisterDirect<AccentColorSelector, bool>(
            nameof(UseCustomAccentColor),
            o => o.UseCustomAccentColor,
            (o, v) => o.UseCustomAccentColor = v);

    public bool UseCustomAccentColor
    {
        get;
        set => SetAndRaise(UseCustomAccentColorProperty, ref field, value);
    }

    public static readonly DirectProperty<AccentColorSelector, Color> CustomAccentColorProperty =
        AvaloniaProperty.RegisterDirect<AccentColorSelector, Color>(
        nameof(CustomAccentColor),
        o => o.CustomAccentColor,
        (o, v) => o.CustomAccentColor = v);

    public Color CustomAccentColor
    {
        get;
        set => SetAndRaise(CustomAccentColorProperty, ref field, value);
    }

    private bool _isHandlingPropertyChange;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_isHandlingPropertyChange) return;

        try
        {
            if (change.Property == SelectedColorProperty)
            {
                _isHandlingPropertyChange = true;

                if (change.NewValue is Color color)
                {
                    if (PresetColors.Contains(color))
                    {
                        SelectedPresetColor = color;
                        UseSystemAccentColor = false;
                        UseCustomAccentColor = false;
                    }
                    else
                    {
                        SelectedPresetColor = null;
                        UseSystemAccentColor = false;
                        UseCustomAccentColor = true;
                    }
                }
                else // null
                {
                    SelectedPresetColor = null;
                    UseSystemAccentColor = true;
                    UseCustomAccentColor = false;
                }
            }
            else if (change.Property == UseSystemAccentColorProperty)
            {
                if (change.NewValue is true)
                {
                    _isHandlingPropertyChange = true;

                    SelectedColor = null;
                    SelectedPresetColor = null;
                    UseCustomAccentColor = false;
                }
            }
            else if (change.Property == SelectedPresetColorProperty)
            {
                if (change.NewValue is Color color)
                {
                    _isHandlingPropertyChange = true;

                    SelectedColor = color;
                    UseSystemAccentColor = false;
                    UseCustomAccentColor = false;
                }
            }
            else if (change.Property == UseCustomAccentColorProperty)
            {
                if (change.NewValue is true)
                {
                    _isHandlingPropertyChange = true;

                    SelectedPresetColor = null;
                    UseSystemAccentColor = false;
                }
            }
            else if (change.Property == CustomAccentColorProperty)
            {
                if (UseCustomAccentColor)
                {
                    _isHandlingPropertyChange = true;

                    SelectedColor = change.NewValue as Color?;
                }
            }
        }
        finally
        {
            _isHandlingPropertyChange = false;
        }
    }
}