using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.StrategyEngine;
using Everywhere.Utilities;
using Lucide.Avalonia;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// Owns the toolbar action editor and its UI-thread-affine, incrementally updated rows.
/// </summary>
public sealed partial class TextSelectionToolbarActionsControl(TextSelectionToolbarActions actions) : TemplatedControl
{
    public static readonly DirectProperty<TextSelectionToolbarActionsControl, IReadOnlyBindableList<Item>> ItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<TextSelectionToolbarActionsControl, IReadOnlyBindableList<Item>>(
            nameof(ItemsSource), control => control.ItemsSource);

    public IReadOnlyBindableList<Item> ItemsSource
    {
        get => _itemsSource;
        private set => SetAndRaise(ItemsSourceProperty, ref _itemsSource, value);
    }

    private IReadOnlyBindableList<Item> _itemsSource = new BindableList<Item>();
    private IDisposable? _itemsSubscription;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        actions.EnsureBuiltInActions();

        ItemsSource = actions.Items
            .ToObservableChangeSet()
            .Transform(action => new Item(action, actions.GetBuiltInStrategy(action)))
            .DisposeMany()
            .BindEx(out var subscription);
        _itemsSubscription = subscription;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _dropHandler.Clear();
        DisposeHelper.DisposeToDefault(ref _itemsSubscription);
        ItemsSource = new BindableList<Item>();
        base.OnDetachedFromVisualTree(e);
    }

    [RelayCommand]
    private async Task AddActionAsync()
    {
        var action = new TextSelectionToolbarAction();
        if (!await EditActionAsync(action, LocaleResolver.TextSelectionToolbarActions_Add)) return;

        // New actions start at the front so they are immediately visible even when the toolbar is full.
        actions.Items.Insert(0, action);
    }

    [RelayCommand]
    private async Task EditActionAsync(Item item) =>
        await EditActionAsync(item.Action, LocaleResolver.TextSelectionToolbarActions_Edit);

    private async Task<bool> EditActionAsync(TextSelectionToolbarAction action, string title)
    {
        var builtInStrategy = actions.GetBuiltInStrategy(action);
        var form = new TextSelectionToolbarActionForm(action, builtInStrategy);
        var result = await DialogManager.CreateDialog(form, title, TopLevel.GetTopLevel(this))
            .WithPrimaryButton(LocaleResolver.Common_OK, (_, e) => e.Cancel = !form.Editor.Validate())
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync();
        if (result != DialogResult.Primary) return false;

        form.Editor.ApplyTo(action, builtInStrategy);
        return true;
    }

    [RelayCommand]
    private void MoveUp(Item item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(Item item) => Move(item, 1);

    private void Move(Item item, int offset)
    {
        var index = actions.Items.IndexOf(item.Action);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= actions.Items.Count) return;

        actions.Items.Move(index, target);
    }

    [RelayCommand]
    private void ResetAction(Item item)
    {
        if (!item.Action.IsBuiltIn) return;

        item.Action.Name = null;
        item.Action.Icon = null;
        item.Action.Prompt = null;
    }

    [RelayCommand]
    private async Task DeleteActionAsync(Item item)
    {
        if (item.Action.IsBuiltIn) return;

        var result = await DialogManager.CreateDialog(
                LocaleResolver.TextSelectionToolbarActions_DeleteConfirmation.Format(item.NameKey.ToString()),
                LocaleResolver.Common_Delete,
                TopLevel.GetTopLevel(this))
            .WithPrimaryButton(LocaleResolver.Common_Delete, buttonStyle: ButtonStyle.Destructive)
            .WithCancelButton(LocaleResolver.Common_Cancel)
            .ShowAsync();
        if (result == DialogResult.Primary) actions.Items.Remove(item.Action);
    }

    public sealed partial class Item : ObservableObject, IDisposable
    {
        public TextSelectionToolbarAction Action { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDropBefore), nameof(IsDropAfter))]
        public partial int DropPosition { get; set; }

        public bool IsDropBefore => DropPosition < 0;

        public bool IsDropAfter => DropPosition > 0;

        public IDynamicLocaleKey NameKey =>
            string.IsNullOrWhiteSpace(Action.Name) ?
                _builtInStrategy?.NameKey ?? new DynamicLocaleKey(LocaleKey.TextSelectionToolbarActions_Unnamed) :
                new DirectLocaleKey(Action.Name);

        public IDynamicLocaleKey? DescriptionKey => _builtInStrategy?.DescriptionKey;

        public ColoredIcon Icon => Action.Icon ?? _builtInStrategy?.Icon ?? _defaultIcon;

        private readonly Strategy? _builtInStrategy;
        private readonly ColoredIcon _defaultIcon = LucideIconKind.Sparkles;

        public Item(TextSelectionToolbarAction action, Strategy? builtInStrategy)
        {
            Action = action;
            _builtInStrategy = builtInStrategy;
            Action.PropertyChanged += HandleActionChanged;
        }

        public void Dispose() => Action.PropertyChanged -= HandleActionChanged;

        private void HandleActionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TextSelectionToolbarAction.Name)) OnPropertyChanged(nameof(NameKey));
            if (e.PropertyName is nameof(TextSelectionToolbarAction.Icon)) OnPropertyChanged(nameof(Icon));
        }
    }
}
