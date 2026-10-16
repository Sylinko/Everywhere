using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.StrategyEngine;
using Lucide.Avalonia;

namespace Everywhere.TextSelection;

/// <summary>
/// A user-defined prompt tool, independent of the registered built-in strategies.
/// </summary>
public sealed partial class CustomTextSelectionToolbarAction : TextSelectionToolbarAction
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameKey), nameof(IsAvailable))]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(TextSelectionToolbarAction), nameof(ValidateRequiredText))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayIcon))]
    public partial ColoredIcon Icon { get; set; } = LucideIconKind.Sparkles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(TextSelectionToolbarAction), nameof(ValidateRequiredText))]
    public partial string Prompt { get; set; } = string.Empty;

    [JsonIgnore]
    public override IDynamicLocaleKey NameKey => string.IsNullOrWhiteSpace(Name) ? UnnamedKey : new DirectLocaleKey(Name.Trim());

    [JsonIgnore]
    public override ColoredIcon DisplayIcon => Icon;

    [JsonIgnore]
    public override bool IsAvailable => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Prompt);

    /// <inheritdoc />
    public override Strategy? CreateStrategy(IReadOnlyDictionary<string, Strategy> matchingStrategies)
    {
        if (!IsAvailable) return null;

        return new Strategy { Id = $"toolbar.{Id:D}", NameKey = NameKey, Icon = Icon, Body = Prompt };
    }

    /// <inheritdoc />
    public override TextSelectionToolbarAction CreateDraft()
    {
        return new CustomTextSelectionToolbarAction
        {
            Id = Id,
            IsEnabled = IsEnabled,
            IsLabelVisible = IsLabelVisible,
            Name = Name,
            Prompt = Prompt,
            Icon = CopyIcon(Icon)
        };
    }

    /// <inheritdoc />
    public override void ApplyTo(TextSelectionToolbarAction target)
    {
        var action = (CustomTextSelectionToolbarAction)target;
        action.Name = Name.Trim();
        action.Prompt = Prompt;
        action.Icon = CopyIcon(Icon);
        action.IsLabelVisible = IsLabelVisible;
    }
}