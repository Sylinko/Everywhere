namespace Everywhere.Configuration;

/// <summary>
/// Marks a settings category class for which the source generator will create SettingsItems property.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GeneratedSettingsItemsAttribute : Attribute;

/// <summary>
/// This attribute is used to mark properties that should not be serialized or displayed in the UI.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class SettingsItemIgnoreAttribute : Attribute;

/// <summary>
/// Additional metadata for a settings item.
/// </summary>
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
    /// Groups multiple settings items under a common header.
    /// Items with the same non-null Group value will be wrapped in a <see cref="SettingsGroupItem"/>.
    /// If starts with "_", group header will hidden.
    /// </summary>
    public string? Group { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public class SettingsStringItemAttribute : Attribute
{
    public string? Watermark { get; set; }
    public int MaxLength { get; set; } = int.MaxValue;
    public bool IsMultiline { get; set; }
    public bool IsPassword { get; set; }
    public double Height { get; set; } = double.NaN;
    public double MinWidth { get; set; } = 320d;
}

[AttributeUsage(AttributeTargets.Property)]
public class SettingsIntegerItemAttribute : Attribute
{
    public int Min { get; set; } = int.MinValue;
    public int Max { get; set; } = int.MaxValue;
    public bool IsSliderVisible { get; set; } = true;
    public bool IsTextBoxVisible { get; set; } = true;
}

[AttributeUsage(AttributeTargets.Property)]
public class SettingsDoubleItemAttribute : Attribute
{
    public double Min { get; set; } = double.NegativeInfinity;
    public double Max { get; set; } = double.PositiveInfinity;
    public double Step { get; set; } = 1.0d;
    public bool IsSliderVisible { get; set; } = true;
    public bool IsTextBoxVisible { get; set; } = true;
}

[AttributeUsage(AttributeTargets.Property)]
public class SettingsSelectionItemAttribute(string itemsSourceBindingPath) : Attribute
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
public class SettingsTemplatedItemAttribute : Attribute
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
/// Indicates this Property should generate items
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

public enum SettingsCollectionBinding
{
    /// <summary>
    /// Keep the collection instance and replace its items to match the JSON array.
    /// </summary>
    ReplaceItems,

    /// <summary>
    /// Reserved for future keyed item patching.
    /// </summary>
    PatchByKey
}

/// <summary>
/// Controls how a settings collection is synchronized from JSON.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingsCollectionBindingAttribute(SettingsCollectionBinding binding) : Attribute
{
    public SettingsCollectionBinding Binding { get; } = binding;
}