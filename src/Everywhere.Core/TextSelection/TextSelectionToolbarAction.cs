using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.StrategyEngine;
using Lucide.Avalonia;

namespace Everywhere.TextSelection;

/// <summary>
/// A persisted toolbar entry with stable identity and directly bindable presentation state.
/// </summary>
[SettingsSerializedSubtree]
[JsonPolymorphic]
[JsonDerivedType(typeof(BuiltInTextSelectionToolbarAction), "built-in")]
[JsonDerivedType(typeof(CustomTextSelectionToolbarAction), "custom")]
public abstract partial class TextSelectionToolbarAction : ObservableValidator
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the toolbar button displays its name. Menu items always display a name.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLabelVisible { get; set; } = true;

    [JsonIgnore]
    public abstract IDynamicLocaleKey NameKey { get; }

    [JsonIgnore]
    public virtual IDynamicLocaleKey DescriptionKey => DirectLocaleKey.Empty;

    [JsonIgnore]
    public abstract ColoredIcon DisplayIcon { get; }

    /// <summary>
    /// Indicates whether this entry can appear in the preview. Runtime availability also depends on matching.
    /// </summary>
    [JsonIgnore]
    public abstract bool IsAvailable { get; }

    protected static IDynamicLocaleKey UnnamedKey { get; } = new DynamicLocaleKey(LocaleKey.TextSelectionToolbarActions_Unnamed);

    protected static ColoredIcon FallbackIcon => LucideIconKind.Sparkles;

    /// <summary>
    /// Creates the execution snapshot from the strategies matched against the captured selection.
    /// </summary>
    public abstract Strategy? CreateStrategy(IReadOnlyDictionary<string, Strategy> matchingStrategies);

    /// <summary>
    /// Creates an isolated editor copy, including mutable icon data.
    /// </summary>
    public abstract TextSelectionToolbarAction CreateDraft();

    /// <summary>
    /// Applies this editor copy to its original entry without replacing the original object.
    /// The target must have the same concrete type as this draft.
    /// </summary>
    public abstract void ApplyTo(TextSelectionToolbarAction target);

    /// <summary>
    /// Validates all editable properties before accepting a draft.
    /// </summary>
    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    /// <summary>
    /// Validates user-entered names and prompts through the binding validation pipeline.
    /// </summary>
    public static ValidationResult? ValidateRequiredText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ?
            new ValidationResult(LocaleKey.ValidationErrorMessage_Required.I18N()) :
            ValidationResult.Success;
    }

    protected static ColoredIcon CopyIcon(ColoredIcon icon)
    {
        return new ColoredIcon(icon.Type, icon.Foreground, icon.Background) { Kind = icon.Kind, Text = icon.Text };
    }
}