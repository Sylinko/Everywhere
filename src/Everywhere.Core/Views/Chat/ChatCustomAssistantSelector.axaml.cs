using Avalonia.Controls.Primitives;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.Messages;

namespace Everywhere.Views;

public sealed partial class ChatCustomAssistantSelector : TemplatedControl
{
    public static readonly StyledProperty<IReadOnlyList<CustomAssistant>?> ItemsSourceProperty =
        AvaloniaProperty.Register<ChatCustomAssistantSelector, IReadOnlyList<CustomAssistant>?>(nameof(ItemsSource));

    public IReadOnlyList<CustomAssistant>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly StyledProperty<CustomAssistant?> SelectedItemProperty =
        AvaloniaProperty.Register<ChatCustomAssistantSelector, CustomAssistant?>(nameof(SelectedItem));

    public CustomAssistant? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public static readonly DirectProperty<ChatCustomAssistantSelector, CustomAssistant?> SelectedSettingsItemProperty =
        AvaloniaProperty.RegisterDirect<ChatCustomAssistantSelector, CustomAssistant?>(
            nameof(SelectedSettingsItem),
            o => o.SelectedSettingsItem);

    public CustomAssistant? SelectedSettingsItem
    {
        get;
        private set => SetAndRaise(SelectedSettingsItemProperty, ref field, value);
    }

    [RelayCommand]
    private void SetSelectedItem(CustomAssistant item)
    {
        if (SelectedItem == item)
        {
            SelectedSettingsItem = item; // Click again, enter the settings page of the assistant.
        }
        else
        {
            SelectedItem = item;
        }
    }

    [RelayCommand]
    private void SetSelectedSettingsItem(CustomAssistant? item)
    {
        SelectedSettingsItem = item;
    }

    [RelayCommand]
    private static void OpenAssistantsSettings()
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(
            new ShowWindowMessage(ShowWindowMessage.MainWindow, MainViewNavigateMessage.CustomAssistantPageRoute));
    }

    [RelayCommand]
    private static void OpenSettings()
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(
            new ShowWindowMessage(ShowWindowMessage.MainWindow, MainViewNavigateMessage.SettingsPageRoute));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var assistants = ItemsSource;
        if (assistants is null || assistants.Count <= 1)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        var currentIndex = SelectedItem is not null ? assistants.IndexOf(SelectedItem) : -1;
        if (currentIndex == -1)
        {
            SelectedItem = assistants[0];
            e.Handled = true;
            return;
        }

        currentIndex = e.Delta.Y switch
        {
            > 0 => Math.Max(currentIndex - 1, 0),
            < 0 => Math.Min(currentIndex + 1, assistants.Count - 1),
            _ => currentIndex
        };

        SelectedItem = assistants[currentIndex];
        e.Handled = true;

        base.OnPointerWheelChanged(e);
    }
}