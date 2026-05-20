using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using ZLinq;

namespace Everywhere.Configuration;

/// <summary>
/// Represents a single settings item for View.
/// </summary>
public abstract class SettingsItem : AvaloniaObject, INotifyDataErrorInfo
{
    public DynamicResourceKey? HeaderKey { get; set; }

    public DynamicResourceKey? DescriptionKey { get; set; }

    public Classes Classes { get; } = [];

    public bool IsExperimental { get; set; }

    public string? DocumentUrl { get; set; }

    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<SettingsItem, object?>(nameof(Value), enableDataValidation: true);

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly StyledProperty<bool> IsEnabledProperty = AvaloniaProperty.Register<SettingsItem, bool>(nameof(IsEnabled), true);

    public bool IsEnabled
    {
        get => GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    public static readonly StyledProperty<bool> IsVisibleProperty = AvaloniaProperty.Register<SettingsItem, bool>(nameof(IsVisible), true);

    public bool IsVisible
    {
        get => GetValue(IsVisibleProperty);
        set => SetValue(IsVisibleProperty, value);
    }

    public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<SettingsItem, bool>(nameof(IsExpanded));

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public static readonly StyledProperty<bool> IsExpandableProperty = AvaloniaProperty.Register<SettingsItem, bool>(nameof(IsExpandable));

    public bool IsExpandable
    {
        get => GetValue(IsExpandableProperty);
        set => SetValue(IsExpandableProperty, value);
    }

    public static readonly DirectProperty<SettingsItem, IEnumerable<SettingsItem>?> ChildrenProperty =
        AvaloniaProperty.RegisterDirect<SettingsItem, IEnumerable<SettingsItem>?>(
            nameof(Children),
            o => o.Children,
            (o, v) => o.Children = v);

    public IEnumerable<SettingsItem>? Children
    {
        get;
        set => SetAndRaise(ChildrenProperty, ref field, value);
    }

    /// <summary>
    /// Indicates whether this settings item contains no content (but may have child items).
    /// </summary>
    public virtual bool IsEmpty => false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsExpandableProperty)
        {
            IsExpanded = change.NewValue is true;
        }
    }

    #region INotifyDataErrorInfo implementation

    private object? _error;

    public IEnumerable GetErrors(string? propertyName) => _error is not null ? new[] { _error } : [];

    public static readonly DirectProperty<SettingsItem, bool> HasErrorsProperty =
        AvaloniaProperty.RegisterDirect<SettingsItem, bool>(nameof(HasErrors), o => o.HasErrors);

    public bool HasErrors => _error is not null;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    protected override void UpdateDataValidation(AvaloniaProperty property, BindingValueType state, Exception? error)
    {
        if (property == ValueProperty) UpdateValueError(state, error);
    }

    private void UpdateValueError(BindingValueType state, Exception? error)
    {
        var oldError = _error;
        _error = state == BindingValueType.DataValidationError && error is DataValidationException { ErrorData: { } errorData } ? errorData : null;
        if (Equals(oldError, _error)) return;

        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(null));
        RaisePropertyChanged(HasErrorsProperty, false, true);
    }

    #endregion
}

public class SettingsBooleanItem : SettingsItem
{
    public static readonly StyledProperty<bool> IsNullableProperty = AvaloniaProperty.Register<SettingsBooleanItem, bool>(nameof(IsNullable));

    public bool IsNullable
    {
        get => GetValue(IsNullableProperty);
        set => SetValue(IsNullableProperty, value);
    }
}

public class SettingsStringItem : SettingsItem
{
    public static readonly StyledProperty<string?> WatermarkProperty = AvaloniaProperty.Register<SettingsStringItem, string?>(nameof(Watermark));

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public static readonly StyledProperty<int> MaxLengthProperty = AvaloniaProperty.Register<SettingsStringItem, int>(nameof(MaxLength));

    public int MaxLength
    {
        get => GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    public static readonly StyledProperty<bool> IsMultilineProperty = AvaloniaProperty.Register<SettingsStringItem, bool>(nameof(IsMultiline));

    public bool IsMultiline
    {
        get => GetValue(IsMultilineProperty);
        set => SetValue(IsMultilineProperty, value);
    }

    public static readonly StyledProperty<TextWrapping> TextWrappingProperty =
        AvaloniaProperty.Register<SettingsStringItem, TextWrapping>(nameof(TextWrapping));

    public TextWrapping TextWrapping
    {
        get => GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public static readonly StyledProperty<char> PasswordCharProperty = AvaloniaProperty.Register<SettingsStringItem, char>(nameof(PasswordChar));

    public char PasswordChar
    {
        get => GetValue(PasswordCharProperty);
        set => SetValue(PasswordCharProperty, value);
    }

    public static readonly StyledProperty<double> HeightProperty = AvaloniaProperty.Register<SettingsStringItem, double>(nameof(Height));

    public double Height
    {
        get => GetValue(HeightProperty);
        set => SetValue(HeightProperty, value);
    }

    public static readonly StyledProperty<double> MinWidthProperty = AvaloniaProperty.Register<SettingsStringItem, double>(nameof(MinWidth));

    public double MinWidth
    {
        get => GetValue(MinWidthProperty);
        set => SetValue(MinWidthProperty, value);
    }
}

public class SettingsIntegerItem : SettingsItem
{
    public static readonly StyledProperty<int> MinValueProperty = AvaloniaProperty.Register<SettingsIntegerItem, int>(nameof(MinValue));

    public int MinValue
    {
        get => GetValue(MinValueProperty);
        set => SetValue(MinValueProperty, value);
    }

    public static readonly StyledProperty<int> MaxValueProperty = AvaloniaProperty.Register<SettingsIntegerItem, int>(nameof(MaxValue));

    public int MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public static readonly StyledProperty<bool> IsSliderVisibleProperty =
        AvaloniaProperty.Register<SettingsIntegerItem, bool>(nameof(IsSliderVisible));

    public bool IsSliderVisible
    {
        get => GetValue(IsSliderVisibleProperty);
        set => SetValue(IsSliderVisibleProperty, value);
    }

    public static readonly StyledProperty<bool> IsTextBoxVisibleProperty =
        AvaloniaProperty.Register<SettingsIntegerItem, bool>(nameof(IsTextBoxVisible), true);

    public bool IsTextBoxVisible
    {
        get => GetValue(IsTextBoxVisibleProperty);
        set => SetValue(IsTextBoxVisibleProperty, value);
    }
}

public class SettingsDoubleItem : SettingsItem
{
    public static readonly StyledProperty<double> MinValueProperty = AvaloniaProperty.Register<SettingsDoubleItem, double>(nameof(MinValue));

    public double MinValue
    {
        get => GetValue(MinValueProperty);
        set => SetValue(MinValueProperty, value);
    }

    public static readonly StyledProperty<double> MaxValueProperty = AvaloniaProperty.Register<SettingsDoubleItem, double>(nameof(MaxValue));

    public double MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<SettingsDoubleItem, double>(nameof(Step));

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public static readonly StyledProperty<bool> IsSliderVisibleProperty =
        AvaloniaProperty.Register<SettingsDoubleItem, bool>(nameof(IsSliderVisible));

    public bool IsSliderVisible
    {
        get => GetValue(IsSliderVisibleProperty);
        set => SetValue(IsSliderVisibleProperty, value);
    }

    public static readonly StyledProperty<bool> IsTextBoxVisibleProperty =
        AvaloniaProperty.Register<SettingsDoubleItem, bool>(nameof(IsTextBoxVisible), true);

    public bool IsTextBoxVisible
    {
        get => GetValue(IsTextBoxVisibleProperty);
        set => SetValue(IsTextBoxVisibleProperty, value);
    }

    public static readonly DirectProperty<SettingsDoubleItem, string?> ValueTextProperty =
        AvaloniaProperty.RegisterDirect<SettingsDoubleItem, string?>(
            nameof(ValueText),
            o => o.ValueText,
            (o, v) => o.ValueText = v);

    public string? ValueText
    {
        get
        {
            // Get the value and use step to determine the number of decimal places to show
            double value;
            try
            {
                value = Convert.ToDouble(Value);
            }
            catch
            {
                value = 0d;
            }

            var step = Step > 0 ? Step : 0.1; // default to 1 decimal places if step is not set
            var decimalPlaces = (int)Math.Ceiling(-Math.Log10(step));
            return value.ToString($"F{decimalPlaces}");
        }
        set
        {
            if (double.TryParse(value, out var result))
            {
                Value = Math.Clamp(result, MinValue, MaxValue);
            }

            RaisePropertyChanged(ValueTextProperty, null, value);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            RaisePropertyChanged(ValueTextProperty, null, ValueText);
        }
    }
}

public class SettingsSelectionItem : SettingsItem
{
    public record Item(DynamicResourceKey Key, object? Value, IDataTemplate? ContentTemplate);

    public static readonly StyledProperty<IEnumerable<Item>> ItemsSourceProperty =
        AvaloniaProperty.Register<SettingsSelectionItem, IEnumerable<Item>>(nameof(ItemsSource));

    public IEnumerable<Item> ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DirectProperty<SettingsSelectionItem, Item?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<SettingsSelectionItem, Item?>(
            nameof(SelectedItem),
            o => o.SelectedItem,
            (o, v) => o.SelectedItem = v);

    public Item? SelectedItem
    {
        get;
        set
        {
            if (Equals(field, value)) return;
            var oldValue = field;
            field = value;
            RaisePropertyChanged(SelectedItemProperty, oldValue, value);
        }
    }

    public static readonly StyledProperty<bool> UserEditableProperty =
        AvaloniaProperty.Register<SettingsSelectionItem, bool>(nameof(IsEditable));

    public bool IsEditable
    {
        get => GetValue(UserEditableProperty);
        set => SetValue(UserEditableProperty, value);
    }

    private bool _isHandlingPropertyChange;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_isHandlingPropertyChange) return;

        _isHandlingPropertyChange = true;
        try
        {
            if (change.Property == ValueProperty && ItemsSource.AsValueEnumerable().Count() > 0)
            {
                SelectedItem = ItemsSource.FirstOrDefault(i => Equals(i.Value, change.NewValue));
            }
            else if (change.Property == ItemsSourceProperty)
            {
                SelectedItem = change.NewValue.As<IEnumerable<Item>>()?.FirstOrDefault(i => Equals(i.Value, Value));

                if (change.OldValue is INotifyCollectionChanged oldCollection)
                {
                    oldCollection.CollectionChanged -= HandleItemsSourceCollectionChanged;
                }

                if (change.NewValue is INotifyCollectionChanged newCollection)
                {
                    newCollection.CollectionChanged += HandleItemsSourceCollectionChanged;
                }
            }
            else if (change.Property == SelectedItemProperty)
            {
                Value = change.NewValue.As<Item>()?.Value;
            }
        }
        finally
        {
            _isHandlingPropertyChange = false;
        }
    }

    private void HandleItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Value is not { } value ||
            sender.As<IEnumerable<Item>>()?.FirstOrDefault(i => Equals(i.Value, value)) is not { } selectedItem) return;

        SelectedItem = selectedItem;
    }
}

public class SettingsCustomizableItem(SettingsItem customValueItem) : SettingsItem
{
    public SettingsItem CustomValueItem => customValueItem;

    public static readonly StyledProperty<ICommand?> ResetCommandProperty =
        AvaloniaProperty.Register<SettingsCustomizableItem, ICommand?>(nameof(ResetCommand));

    public ICommand? ResetCommand
    {
        get => GetValue(ResetCommandProperty);
        set => SetValue(ResetCommandProperty, value);
    }
}

/// <summary>
/// A settings item that holds a value of a specific data template.
/// </summary>
/// <param name="dataTemplate"></param>
public abstract class SettingsTemplatedItem(IDataTemplate? dataTemplate) : SettingsItem
{
    public IDataTemplate? DataTemplate => dataTemplate;

    /// <summary>
    /// Creates a SettingsTypedItem for the given property type. If no DataTemplate is found, returns an EmptySettingsTypedItem.
    /// </summary>
    /// <param name="propertyType"></param>
    /// <returns></returns>
    public static SettingsTemplatedItem Create(Type propertyType)
    {
        if (Application.Current?.Resources.TryGetResource(propertyType, null, out var resource) is not true ||
            resource is not IDataTemplate dataTemplate)
        {
            return new EmptySettingsTemplatedItem();
        }

        var typedItem = typeof(SettingsTemplatedItem<>).MakeGenericType(propertyType);
        var constructor = typedItem.GetConstructor([typeof(IDataTemplate)]);
        return (SettingsTemplatedItem?)constructor?.Invoke([dataTemplate]) ?? new EmptySettingsTemplatedItem();
    }
}

/// <summary>
/// Stands for a SettingsTypedItem with no specific type, usually used as a placeholder.
/// </summary>
public sealed class EmptySettingsTemplatedItem() : SettingsTemplatedItem(null)
{
    public override bool IsEmpty => true;
}

/// <summary>
/// A settings item that holds a value of a specific data template.
/// TType is used for DataTemplate selection.
/// </summary>
/// <typeparam name="TType"></typeparam>
public sealed class SettingsTemplatedItem<TType>(IDataTemplate? dataTemplate) : SettingsTemplatedItem(dataTemplate);

/// <summary>
/// A settings item that contains a custom control.
/// </summary>
/// <param name="controlFactory"></param>
public sealed class SettingsControlItem(Func<Control> controlFactory) : SettingsItem
{
    /// <summary>
    /// Use lazy control creation to avoid unnecessary instantiation and potential UI thread issues.
    /// </summary>
    public Control Control => controlFactory();
}

/// <summary>
/// A settings item that groups multiple child items under a common header.
/// The group is always expandable and arranges its children vertically.
/// </summary>
public sealed class SettingsGroupItem : SettingsItem
{
    /// <summary>
    /// Gets the name of the group, used as the display header.
    /// </summary>
    public string? GroupName { get; init; }

    /// <summary>
    /// Gets the localized group key
    /// </summary>
    public IDynamicResourceKey GroupKey => GroupName?.StartsWith('_') is not false ? DirectResourceKey.Empty : new DynamicResourceKey(GroupName);

    /// <summary>
    /// A group item has no value of its own; it only serves as a container.
    /// </summary>
    public override bool IsEmpty => true;

    public SettingsGroupItem()
    {
        IsExpandable = true;
        IsExpanded = true;
    }
}