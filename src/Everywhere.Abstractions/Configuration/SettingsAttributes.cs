namespace Everywhere.Configuration;

/// <summary>
/// Marks a settings category class for which the source generator will create SettingsItems property.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GeneratedSettingsItemsAttribute : Attribute
{
    /// <summary>
    /// Gets or sets whether settings metadata declared by base types is included.
    /// </summary>
    public bool IncludeInheritedMembers { get; set; } = true;
}

/// <summary>
/// This attribute is used to mark properties that should not be serialized or displayed in the UI.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class SettingsItemIgnoreAttribute : Attribute;

/// <summary>
/// Common presentation metadata for a single settings item.
/// Use one declaration per property, either this attribute or a specialized derived attribute.
/// </summary>
/// <remarks>
/// Binding paths are emitted as typed expressions rooted at the owning settings object.
/// Boolean bindings also support !, &amp;&amp;, ||, and parentheses. Logical combinations
/// use MultiBinding with compiled property bindings rather than a single lambda expression.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public class SettingsItemAttribute : Attribute
{
    /// <summary>
    /// Sets a binding path that will be used to determine if this item is visible in the UI.
    /// </summary>
    public string? IsVisibleBindingPath { get; set; }

    /// <summary>
    /// Sets a binding path that will be used to determine if this item is enabled in the UI.
    /// </summary>
    public string? IsEnabledBindingPath { get; set; }

    /// <summary>
    /// Sets custom classes to apply to the settings item in the UI.
    /// </summary>
    public string[]? Classes { get; set; }

    /// <summary>
    /// Marks this settings item as experimental.
    /// </summary>
    public bool IsExperimental { get; set; }

    /// <summary>
    /// Optional documentation URL for this settings item.
    /// </summary>
    public string? DocumentUrl { get; set; }

    /// <summary>
    /// Sets a binding path that will be used to determine the documentation URL for this item in the UI.
    /// </summary>
    public string? DocumentUrlBindingPath { get; set; }

    /// <summary>
    /// An optional method name used to modify or replace the generated settings item before it is added to the UI.
    /// </summary>
    public string? Modifier { get; set; }

    /// <summary>
    /// Groups multiple settings items under a common header.
    /// Items with the same non-null Group value will be wrapped in a <see cref="SettingsGroupItem"/>.
    /// If starts with "_", group header will hidden.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Gets or sets the order of this item among settings items at the same level.
    /// Lower values are emitted first; items with the same value keep their existing order.
    /// </summary>
    public int Index { get; set; } = int.MinValue;
}

/// <summary>
/// Configures a string settings item and its common presentation metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SettingsStringItemAttribute : SettingsItemAttribute
{
    /// <summary>
    /// Gets or sets the placeholder text for the string input field in the UI.
    /// </summary>
    public string? PlaceholderText { get; set; }

    /// <summary>
    /// Gets or sets the maximum length of the string input field in the UI.
    /// </summary>
    public int MaxLength { get; set; } = int.MaxValue;

    /// <summary>
    /// Gets or sets whether the string input field should support multiple lines in the UI.
    /// </summary>
    public bool IsMultiline { get; set; }

    /// <summary>
    /// Gets or sets whether the string input field should be treated as a password field in the UI.
    /// </summary>
    public bool IsPassword { get; set; }

    /// <summary>
    /// Gets or sets the width of the string input field in the UI. If not set, the default width will be used.
    /// </summary>
    public double Height { get; set; } = double.NaN;

    /// <summary>
    /// Gets or sets the minimum width of the string input field in the UI. If not set, the default minimum width will be used.
    /// </summary>
    public double MinWidth { get; set; } = 320d;
}

/// <summary>
/// Configures an integer settings item and its common presentation metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SettingsIntegerItemAttribute : SettingsItemAttribute
{
    /// <summary>
    /// Gets or sets the minimum value for the slider and the integer input field in the UI.
    /// </summary>
    public int Min { get; set; } = int.MinValue;

    /// <summary>
    /// Gets or sets the maximum value for the slider and the integer input field in the UI.
    /// </summary>
    public int Max { get; set; } = int.MaxValue;

    /// <summary>
    /// Gets or sets whether the slider should be displayed in the UI. If false, only the integer input field will be shown.
    /// </summary>
    public bool IsSliderVisible { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the integer input field should be displayed as a text box in the UI. If false, only the slider will be shown.
    /// </summary>
    public bool IsTextBoxVisible { get; set; } = true;
}

/// <summary>
/// Configures a floating-point settings item and its common presentation metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SettingsDoubleItemAttribute : SettingsItemAttribute
{
    /// <summary>
    /// Gets or sets the minimum value for the slider and the double input field in the UI.
    /// </summary>
    public double Min { get; set; } = double.NegativeInfinity;

    /// <summary>
    /// Gets or sets the maximum value for the slider and the double input field in the UI.
    /// </summary>
    public double Max { get; set; } = double.PositiveInfinity;

    /// <summary>
    /// Gets or sets the step value for the slider and the double input field in the UI. This determines the increment/decrement amount when adjusting the value.
    /// </summary>
    public double Step { get; set; } = 1.0d;

    /// <summary>
    /// Gets or sets whether the slider should be displayed in the UI. If false, only the double input field will be shown.
    /// </summary>
    public bool IsSliderVisible { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the double input field should be displayed as a text box in the UI. If false, only the slider will be shown.
    /// </summary>
    public bool IsTextBoxVisible { get; set; } = true;
}

/// <summary>
/// Configures a selection settings item and its common presentation metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SettingsSelectionItemAttribute(string itemsSourceBindingPath) : SettingsItemAttribute
{
    /// <summary>
    /// A binding path to the property that contains the items to select from.
    /// </summary>
    public string ItemsSourceBindingPath { get; set; } = itemsSourceBindingPath;

    /// <summary>
    /// Should look for i18n keys in the items source
    /// </summary>
    public bool I18N { get; set; }

    /// <summary>
    /// An optional key to use for the DataTemplate to display each item.
    /// </summary>
    public object? DataTemplateKey { get; set; }

    /// <summary>
    /// Can user edit combobox
    /// </summary>
    /// TODO: This is complicated to impl
    public bool IsEditable { get; set; }
}

/// <summary>
/// Indicates this Property is a single item that should use a DataTemplate for display
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SettingsTemplatedItemAttribute : SettingsItemAttribute
{
    /// <summary>
    /// An optional key to use for the DataTemplate to display the item.
    /// If null, typeof the property will be used as the key.
    /// </summary>
    public object? DataTemplateKey { get; set; }

    public SettingsTemplatedItemAttribute() { }

    public SettingsTemplatedItemAttribute(object dataTemplateKey)
    {
        DataTemplateKey = dataTemplateKey;
    }
}

/// <summary>
/// Adds expandable child settings to a property. This composes with a single settings item declaration.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SettingsItemsAttribute : Attribute
{
    public bool IsExpanded { get; set; }

    public string? IsExpandableBindingPath { get; set; }
}

/// <summary>
/// Marks a settings property or type as a terminal System.Text.Json subtree.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property)]
public sealed class SettingsSerializedSubtreeAttribute : Attribute;

public enum SettingsUnknownMemberHandling
{
    /// <summary>
    /// Keep unknown JSON keys in the backing document.
    /// </summary>
    Preserve,

    /// <summary>
    /// Remove JSON keys that are not represented by the effective descriptor.
    /// </summary>
    Prune,

    /// <summary>
    /// Report a diagnostic when unknown JSON keys are present.
    /// </summary>
    Error
}

/// <summary>
/// Controls how unknown JSON members are handled for a settings object subtree.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property)]
public sealed class SettingsUnknownMemberHandlingAttribute(SettingsUnknownMemberHandling handling) : Attribute
{
    public SettingsUnknownMemberHandling Handling { get; } = handling;
}
