using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.StrategyEngine;

namespace Everywhere.TextSelection;

/// <summary>
/// References a registered strategy. Null overrides continue to inherit its defaults.
/// </summary>
public sealed partial class BuiltInTextSelectionToolbarAction : TextSelectionToolbarAction
{
    /// <summary>
    /// The provider-qualified configuration identity, distinct from the execution strategy's intent ID.
    /// </summary>
    public required string StrategyId { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameKey))]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(BuiltInTextSelectionToolbarAction), nameof(ValidateOverride))]
    public partial string? NameOverride { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayIcon))]
    public partial ColoredIcon? IconOverride { get; set; }

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(BuiltInTextSelectionToolbarAction), nameof(ValidateOverride))]
    public partial string? PromptOverride { get; set; }

    /// <summary>
    /// Registered metadata resolved by the manager after loading or replacing a persisted entry.
    /// </summary>
    [JsonIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameKey), nameof(DescriptionKey), nameof(DisplayIcon), nameof(IsAvailable))]
    public partial Strategy? DefaultStrategy { get; set; }

    [JsonIgnore]
    public override IDynamicLocaleKey NameKey =>
        string.IsNullOrWhiteSpace(NameOverride) ?
            DefaultStrategy?.NameKey ?? UnnamedKey :
            new DirectLocaleKey(NameOverride.Trim());

    [JsonIgnore]
    public override IDynamicLocaleKey DescriptionKey => DefaultStrategy?.DescriptionKey ?? DirectLocaleKey.Empty;

    [JsonIgnore]
    public override ColoredIcon DisplayIcon => IconOverride ?? DefaultStrategy?.Icon ?? FallbackIcon;

    [JsonIgnore]
    public override bool IsAvailable => DefaultStrategy is not null;

    private string? _draftDefaultName;

    /// <inheritdoc />
    public override Strategy? CreateStrategy(IReadOnlyDictionary<string, Strategy> matchingStrategies)
    {
        if (!matchingStrategies.TryGetValue(StrategyId, out var strategy)) return null;

        // Clone the matched strategy, retaining conditions, preprocessors, system prompt and tool permissions.
        return strategy with
        {
            NameKey = string.IsNullOrWhiteSpace(NameOverride) ? strategy.NameKey : new DirectLocaleKey(NameOverride.Trim()),
            Icon = IconOverride ?? strategy.Icon,
            Body = string.IsNullOrWhiteSpace(PromptOverride) ? strategy.Body : PromptOverride
        };
    }

    /// <inheritdoc />
    public override TextSelectionToolbarAction CreateDraft()
    {
        // Capture the inherited name once for this dialog. A locale change while editing must not
        // turn an unchanged inherited name into a permanent override when the user accepts it.
        var defaultName = DefaultStrategy?.NameKey.ToString();
        return new BuiltInTextSelectionToolbarAction
        {
            Id = Id,
            StrategyId = StrategyId,
            IsEnabled = IsEnabled,
            IsLabelVisible = IsLabelVisible,
            DefaultStrategy = DefaultStrategy,
            NameOverride = NameOverride ?? defaultName,
            PromptOverride = PromptOverride ?? DefaultStrategy?.Body,
            IconOverride = CopyIcon(DisplayIcon),
            _draftDefaultName = defaultName
        };
    }

    /// <inheritdoc />
    public override void ApplyTo(TextSelectionToolbarAction target)
    {
        var action = (BuiltInTextSelectionToolbarAction)target;
        var name = NameOverride?.Trim();
        var defaultIcon = DefaultStrategy?.Icon ?? FallbackIcon;
        var icon = DisplayIcon;

        action.NameOverride = name == _draftDefaultName ? null : name;
        action.PromptOverride = PromptOverride == DefaultStrategy?.Body ? null : PromptOverride;
        action.IconOverride = icon.Type == defaultIcon.Type && icon.Kind == defaultIcon.Kind &&
            icon.Text == defaultIcon.Text && icon.Foreground == defaultIcon.Foreground && icon.Background == defaultIcon.Background ?
                null :
                CopyIcon(icon);
        action.IsLabelVisible = IsLabelVisible;
    }

    /// <summary>
    /// Restores inherited presentation and prompt defaults without changing order or enabled state.
    /// </summary>
    [RelayCommand]
    public void Reset()
    {
        NameOverride = null;
        IconOverride = null;
        PromptOverride = null;
        IsLabelVisible = true;
    }

    /// <summary>
    /// Allows inheritance while rejecting explicitly entered blank overrides.
    /// </summary>
    public static ValidationResult? ValidateOverride(string? value)
    {
        return value is null ? ValidationResult.Success : ValidateRequiredText(value);
    }
}