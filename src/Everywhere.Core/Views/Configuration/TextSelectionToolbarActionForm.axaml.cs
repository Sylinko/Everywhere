using System.ComponentModel.DataAnnotations;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.StrategyEngine;
using Lucide.Avalonia;

namespace Everywhere.Views;

/// <summary>
/// Edits an isolated draft so cancelling a dialog cannot change the live toolbar or its icon.
/// </summary>
public sealed partial class TextSelectionToolbarActionForm : TemplatedControl
{
    public static readonly DirectProperty<TextSelectionToolbarActionForm, Draft> EditorProperty =
        AvaloniaProperty.RegisterDirect<TextSelectionToolbarActionForm, Draft>(nameof(Editor), form => form.Editor);

    public Draft Editor { get; }

    public TextSelectionToolbarActionForm(TextSelectionToolbarAction action, Strategy? builtInStrategy)
    {
        Editor = new Draft(action, builtInStrategy);
    }

    public sealed partial class Draft : ObservableValidator
    {
        [ObservableProperty]
        [NotifyDataErrorInfo]
        [CustomValidation(typeof(Draft), nameof(ValidateRequiredText))]
        public partial string Name { get; set; }

        [ObservableProperty]
        [NotifyDataErrorInfo]
        [CustomValidation(typeof(Draft), nameof(ValidateRequiredText))]
        public partial string Prompt { get; set; }

        public ColoredIcon Icon { get; }

        public Draft(TextSelectionToolbarAction action, Strategy? builtInStrategy)
        {
            Name = action.Name ?? builtInStrategy?.NameKey.ToString() ?? string.Empty;
            Prompt = action.Prompt ?? builtInStrategy?.Body ?? string.Empty;

            var icon = action.Icon ?? builtInStrategy?.Icon;
            Icon = icon is null ? LucideIconKind.Sparkles : new ColoredIcon(icon.Type, icon.Foreground, icon.Background)
            {
                Kind = icon.Kind,
                Text = icon.Text
            };
        }

        public bool Validate()
        {
            ValidateAllProperties();
            return !HasErrors;
        }

        public void ApplyTo(TextSelectionToolbarAction action, Strategy? builtInStrategy)
        {
            var name = Name.Trim();
            action.Name = name == builtInStrategy?.NameKey.ToString() ? null : name;
            action.Prompt = Prompt == builtInStrategy?.Body ? null : Prompt;
            action.Icon = builtInStrategy?.Icon is { } builtInIcon &&
                Icon.Type == builtInIcon.Type &&
                Icon.Kind == builtInIcon.Kind &&
                Icon.Text == builtInIcon.Text &&
                Icon.Foreground == builtInIcon.Foreground &&
                Icon.Background == builtInIcon.Background ? null : Icon;
        }

        public static ValidationResult? ValidateRequiredText(string? value) =>
            string.IsNullOrWhiteSpace(value) ?
                new ValidationResult(LocaleResolver.ValidationErrorMessage_Required) :
                ValidationResult.Success;
    }
}
