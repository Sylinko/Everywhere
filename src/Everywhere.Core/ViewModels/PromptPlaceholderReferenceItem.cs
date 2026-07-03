using Avalonia.Input.Platform;
using Avalonia.Media;
using Everywhere.AI.Prompts;
using Everywhere.Common;
using Everywhere.Views;
using ShadUI;

namespace Everywhere.ViewModels;

/// <summary>
/// UI item for the reusable prompt placeholder reference list.
/// </summary>
/// <remarks>
/// The item carries the same deterministic brush used by editor and preview highlighting, so users
/// can visually connect a placeholder in the reference list with the same placeholder in the text.
/// </remarks>
public sealed record PromptPlaceholderReferenceItem(
    string PlaceholderText,
    IDynamicLocaleKey DescriptionKey,
    IBrush PlaceholderBrush
)
{
    public static IReadOnlyList<PromptPlaceholderReferenceItem> CreateDefaultItems(
        IReadOnlyList<PromptPlaceholderDefinition> definitions)
    {
        var colorSlots = PromptPlaceholderPalette.AssignColorSlots([.. definitions.Select(static definition => definition.Name)]);
        return
        [
            .. definitions.Select(definition => new PromptPlaceholderReferenceItem(
                "{" + definition.Name + "}",
                definition.DescriptionKey,
                PromptPlaceholderPalette.GetBrush(definition.Name, colorSlots[definition.Name])))
        ];
    }

    public async void CopyToClipboard()
    {
        try
        {
            await App.Clipboard.SetTextAsync(PlaceholderText);
            ToastManager.Success(LocaleResolver.Common_Copied);
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());
        }
    }
}