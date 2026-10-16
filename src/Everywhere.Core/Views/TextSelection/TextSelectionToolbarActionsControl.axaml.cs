using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Configuration;
using Everywhere.StrategyEngine;
using Everywhere.TextSelection;
using ShadUI;
using CustomTextSelectionToolbarAction = Everywhere.TextSelection.CustomTextSelectionToolbarAction;
using TextSelectionToolbarAction = Everywhere.TextSelection.TextSelectionToolbarAction;

namespace Everywhere.Views;

/// <summary>
/// Edits the persisted toolbar actions directly, preserving their identity and display order.
/// </summary>
public sealed partial class TextSelectionToolbarActionsControl(TextSelectionToolbarActionsManager actionsManager) : TemplatedControl
{
    public TextSelectionToolbarActionsManager ActionsManager => actionsManager;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        actionsManager.EnsureBuiltInActions();
    }

    [RelayCommand]
    private async Task AddActionAsync()
    {
        var action = new CustomTextSelectionToolbarAction();
        if (!await EditActionAsync(action, LocaleKey.TextSelectionToolbarActions_Add.I18N())) return;

        // New actions start at the front so they are immediately visible even when the toolbar is full.
        actionsManager.Items.Insert(0, action);
    }

    [RelayCommand]
    private async Task EditActionAsync(TextSelectionToolbarAction action)
    {
        await EditActionAsync(action, LocaleKey.TextSelectionToolbarActions_Edit.I18N());
    }

    private async Task<bool> EditActionAsync(TextSelectionToolbarAction action, string title)
    {
        var form = new TextSelectionToolbarActionForm(action);
        var result = await DialogManager.CreateDialog(form, title, TopLevel.GetTopLevel(this))
            .WithPrimaryButton(LocaleKey.Common_OK.I18N(), (_, e) => e.Cancel = !form.Editor.Validate())
            .WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync();
        if (result != DialogResult.Primary) return false;

        form.Editor.ApplyTo(action);
        return true;
    }

    [RelayCommand]
    private void DeleteAction(CustomTextSelectionToolbarAction action) => actionsManager.Items.Remove(action);
}