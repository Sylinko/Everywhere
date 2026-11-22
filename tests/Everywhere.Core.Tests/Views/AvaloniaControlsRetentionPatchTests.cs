using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Everywhere.Core.Tests.Views;

/// <summary>
/// Verifies the cross-platform <c>Avalonia.Controls</c> retention fixes shipped in <c>Sylinko/Avalonia</c>
/// without altering behavior when controls stay attached or retain unchanged content across navigations.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class AvaloniaControlsRetentionPatchTests
{
    private static readonly string[] ExpectedRemovedLabelsAfterModelReplacement = ["tab-1", "tab-3"];

    [AvaloniaTest]
    public void TransitioningContentControl_ContentChangedWhileDetached_ReleasesPreviousContentAndRendersNewContentOnReattach()
    {
        var transitioning = new TransitioningContentControl();
        transitioning.DataTemplates.Add(CreateTextBlockTemplate());
        var window = CreateWindow(transitioning);
        window.Show();

        try
        {
            var firstRef = AttachInitialContent(window, transitioning, "first");
            Assert.That(GetSingleTextBlock(transitioning).Text, Is.EqualTo("first"));

            // Detach without clearing Content first, simulating navigating away from ChatPluginPage.
            window.Content = new Panel();
            SettleLayout(window);

            ClearTransitioningContent(transitioning);
            CollectAndWait();
            Assert.That(firstRef.IsAlive, Is.False);

            // Replace while still detached, then replace again to ensure intermediate values are also dropped.
            var secondRef = SetTransitioningContent(transitioning, "second");
            SetTransitioningContent(transitioning, "third");
            CollectAndWait();
            Assert.That(secondRef.IsAlive, Is.False);

            window.Content = transitioning;
            SettleLayout(window);
            Assert.That(GetSingleTextBlock(transitioning).Text, Is.EqualTo("third"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void TransitioningContentControl_DetachedWithoutContentChange_KeepsPresenterContentUntilReattach()
    {
        var item = new TestItem("preserved");
        var transitioning = new TransitioningContentControl
        {
            Content = item
        };
        transitioning.DataTemplates.Add(CreateTextBlockTemplate());
        var window = CreateWindow(transitioning);
        window.Show();

        try
        {
            SettleLayout(window);
            Assert.That(GetSingleTextBlock(transitioning).Text, Is.EqualTo("preserved"));

            window.Content = new Panel();
            SettleLayout(window);
            Assert.That(transitioning.Presenter?.Content, Is.SameAs(item));

            window.Content = transitioning;
            SettleLayout(window);
            Assert.That(GetSingleTextBlock(transitioning).Text, Is.EqualTo("preserved"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ContentPresenter_ContentChangedWhileDetached_ClearsDataContextAndRestoresOnReattach()
    {
        var presenter = new ContentPresenter();
        presenter.DataTemplates.Add(CreateTextBlockTemplate());
        var window = CreateWindow(presenter);
        window.Show();

        try
        {
            var firstRef = AttachInitialPresenterContent(window, presenter, "first");
            Assert.That(GetSingleTextBlock(presenter).Text, Is.EqualTo("first"));

            window.Content = new Panel();
            SettleLayout(window);

            ReplacePresenterContentWhileDetached(presenter, "second");
            Assert.That(presenter.DataContext, Is.Null);

            CollectAndWait();
            Assert.That(firstRef.IsAlive, Is.False);

            window.Content = presenter;
            SettleLayout(window);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(presenter.DataContext, Is.TypeOf<TestItem>().With.Property(nameof(TestItem.Label)).EqualTo("second"));
                Assert.That(GetSingleTextBlock(presenter).Text, Is.EqualTo("second"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ListBox_ItemRemovedWhileAttachedOrDetached_ReleasesItemFromRecyclePoolAndTabOnceFocus()
    {
        var items = CreateItems("alpha", "beta", "gamma");
        var listBox = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = CreateToggleSwitchTemplate()
        };
        var window = CreateWindow(listBox, height: 300);
        window.Show();

        try
        {
            SettleLayout(window);
            Assert.That(listBox.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any(), Is.True);

            var alphaRef = FocusChildAndRemoveFirstItem(window, listBox, items);
            CollectAndWait();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(alphaRef.IsAlive, Is.False);
                Assert.That(KeyboardNavigation.GetTabOnceActiveElement(listBox), Is.Null);
            }

            var betaRef = FocusContainerDetachAndRemoveFirstItem(window, listBox, items);
            CollectAndWait();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(betaRef.IsAlive, Is.False);
                Assert.That(KeyboardNavigation.GetTabOnceActiveElement(listBox), Is.Null);
            }

            window.Content = listBox;
            items.Add(new TestItem("delta"));
            SettleLayout(window);

            var containerForDelta = listBox.ContainerFromIndex(1);
            Assert.That(containerForDelta, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(containerForDelta?.DataContext, Is.SameAs(items[1]));
                Assert.That(containerForDelta?.GetVisualDescendants().OfType<ToggleSwitch>().Single().Content, Is.EqualTo("delta"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ListBox_FocusedItemScrolledOffscreenThenRemoved_RecyclesOnceWithoutCorruptingPool()
    {
        var items = new ObservableCollection<TestItem>(
            Enumerable.Range(0, 40).Select(index => new TestItem($"item-{index}")));
        var listBox = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = CreateFixedHeightTemplate(30)
        };
        var window = CreateWindow(listBox, height: 120);
        window.Show();

        try
        {
            SettleLayout(window);

            var firstRef = FocusFirstContainerScrollAwayAndRemove(window, listBox, items);
            CollectAndWait();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstRef.IsAlive, Is.False);
                Assert.That(KeyboardNavigation.GetTabOnceActiveElement(listBox), Is.Null);
            }

            // Exercise the recycle pool back at the top of the list to confirm the container was pushed at most once.
            listBox.ScrollIntoView(0);
            SettleLayout(window);
            items.Add(new TestItem("tail-a"));
            items.Add(new TestItem("tail-b"));
            items.RemoveAt(0);
            SettleLayout(window);

            var visibleTopText = listBox.ContainerFromIndex(0) is { } visibleTop ? GetSingleTextBlock(visibleTop).Text : null;
            Assert.That(visibleTopText, Is.EqualTo("item-2"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void Border_DetachedFromVisualTree_ClearsCompositionBorderVisualAndReleasesRemovedDescendants()
    {
        var borderVisualField = typeof(Border).GetField("_borderVisual", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing Border._borderVisual field.");
        var childGrid = new Grid();
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = childGrid
        };
        var window = CreateWindow(border);
        window.Show();

        try
        {
            var itemRef = AttachChildWithItem(window, childGrid, "composited-child");
            Assert.That(HasBorderVisual(border, borderVisualField), Is.True);

            // Detach Border (simulating an offscreen SmoothBorder in NavigationView) before removing child visuals.
            window.Content = new Panel();
            SettleLayout(window);
            Assert.That(HasBorderVisual(border, borderVisualField), Is.False);

            childGrid.Children.Clear();
            SettleLayout(window);
            CollectAndWait();
            Assert.That(itemRef.IsAlive, Is.False);

            // Reattaching must recreate _borderVisual and keep applying CornerRadius updates.
            window.Content = border;
            border.CornerRadius = new CornerRadius(12);
            SettleLayout(window);
            Assert.That(HasBorderVisual(border, borderVisualField), Is.True);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void TabControl_ItemsClearedAndSelectionModelReplaced_ReleasesSelectedItemsWithoutBreakingEvents()
    {
        var items = CreateItems("tab-1", "tab-2");
        var removedLabels = new List<string>();
        var tabControl = new TestTabControl
        {
            ItemsSource = items,
            ItemTemplate = CreateTextBlockTemplate(),
            ContentTemplate = CreateTextBlockTemplate()
        };
        tabControl.SelectionChanged += (_, e) => removedLabels.AddRange(e.RemovedItems.OfType<TestItem>().Select(item => item.Label));

        var window = CreateWindow(tabControl);
        window.Show();

        try
        {
            SettleLayout(window);
            var firstSelectedRef = GetItemWeakReference(items, 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tabControl.SelectedIndex, Is.Zero);
                Assert.That(tabControl.SelectionMode, Is.EqualTo(SelectionMode.AlwaysSelected));
            }

            ClearItemsAndSettle(window, items);
            CollectAndWait();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(removedLabels, Has.Count.EqualTo(1));
                Assert.That(removedLabels.FirstOrDefault(), Is.EqualTo("tab-1"));
                Assert.That(tabControl.SelectedItem, Is.Null);
                Assert.That(tabControl.SelectedValue, Is.Null);
                Assert.That(firstSelectedRef.IsAlive, Is.False);
            }

            // AlwaysSelected must still auto-select newly inserted items after reset.
            AddItemsAndSettle(window, items, "tab-3", "tab-4");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tabControl.SelectedIndex, Is.Zero);
                Assert.That(GetTabContentText(tabControl), Is.EqualTo("tab-3"));
            }

            var replacedSelectedRef = ReplaceSelectionModelAndClearOldSource(window, tabControl, items);
            CollectAndWait();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(removedLabels, Is.EqualTo(ExpectedRemovedLabelsAfterModelReplacement));
                Assert.That(replacedSelectedRef.IsAlive, Is.False);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachInitialContent(Window window, TransitioningContentControl transitioning, string label)
    {
        var item = new TestItem(label);
        transitioning.Content = item;
        SettleLayout(window);
        return new WeakReference(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearTransitioningContent(TransitioningContentControl transitioning)
    {
        transitioning.Content = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SetTransitioningContent(TransitioningContentControl transitioning, string label)
    {
        var item = new TestItem(label);
        transitioning.Content = item;
        return new WeakReference(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachInitialPresenterContent(Window window, ContentPresenter presenter, string label)
    {
        var item = new TestItem(label);
        presenter.Content = item;
        SettleLayout(window);
        Assert.That(presenter.DataContext, Is.SameAs(item));
        return new WeakReference(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReplacePresenterContentWhileDetached(ContentPresenter presenter, string label)
    {
        presenter.Content = new TestItem(label);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FocusChildAndRemoveFirstItem(
        Window window,
        ListBox listBox,
        ObservableCollection<TestItem> items
    )
    {
        var item = items[0];
        var container = listBox.ContainerFromIndex(0);
        Assert.That(container, Is.Not.Null);
        if (container is null)
        {
            throw new AssertionException("Expected realized container at index 0.");
        }

        var toggle = container.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        KeyboardNavigation.SetTabOnceActiveElement(listBox, toggle);
        Assert.That(KeyboardNavigation.GetTabOnceActiveElement(listBox), Is.SameAs(toggle));

        items.RemoveAt(0);
        SettleLayout(window);
        Assert.That(container.DataContext, Is.Null, "Cleared container kept DataContext.");
        return new WeakReference(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FocusContainerDetachAndRemoveFirstItem(
        Window window,
        ListBox listBox,
        ObservableCollection<TestItem> items
    )
    {
        var item = items[0];
        var container = listBox.ContainerFromIndex(0);
        Assert.That(container, Is.Not.Null);

        KeyboardNavigation.SetTabOnceActiveElement(listBox, container);
        Assert.That(KeyboardNavigation.GetTabOnceActiveElement(listBox), Is.SameAs(container));

        window.Content = new Panel();
        SettleLayout(window);

        items.RemoveAt(0);
        return new WeakReference(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FocusFirstContainerScrollAwayAndRemove(
        Window window,
        ListBox listBox,
        ObservableCollection<TestItem> items
    )
    {
        var item = items[0];
        var container = listBox.ContainerFromIndex(0);
        Assert.That(container, Is.Not.Null);

        KeyboardNavigation.SetTabOnceActiveElement(listBox, container);
        listBox.ScrollIntoView(items.Count - 1);
        SettleLayout(window);

        // Container 0 should have left the realized viewport while staying parked in _focusedElement.
        var panel = listBox.GetVisualDescendants().OfType<VirtualizingStackPanel>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.FirstRealizedIndex, Is.GreaterThan(0));
            Assert.That(listBox.ContainerFromIndex(0), Is.SameAs(container));
        }

        items.RemoveAt(0);
        SettleLayout(window);
        return new WeakReference(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ObservableCollection<TestItem> CreateItems(params string[] labels)
    {
        return [..labels.Select(label => new TestItem(label))];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasBorderVisual(Border border, FieldInfo borderVisualField) =>
        borderVisualField.GetValue(border) is not null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachChildWithItem(Window window, Grid childGrid, string label)
    {
        var item = new TestItem(label);
        childGrid.Children.Add(new TextBlock { DataContext = item, Text = label });
        SettleLayout(window);
        return new WeakReference(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference GetItemWeakReference(ObservableCollection<TestItem> items, int index)
    {
        return new WeakReference(items[index]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearItemsAndSettle(Window window, ObservableCollection<TestItem> items)
    {
        items.Clear();
        SettleLayout(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddItemsAndSettle(Window window, ObservableCollection<TestItem> items, params string[] labels)
    {
        foreach (var label in labels)
        {
            items.Add(new TestItem(label));
        }

        SettleLayout(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReplaceSelectionModelAndClearOldSource(
        Window window,
        TestTabControl tabControl,
        ObservableCollection<TestItem> items
    )
    {
        var previousRef = new WeakReference(items[0]);
        var oldModel = tabControl.SelectionModel;
        tabControl.SelectionMode = SelectionMode.Single;
        tabControl.SelectionModel = new SelectionModel<object?> { SingleSelect = true };
        Assert.That(tabControl.SelectedValue, Is.Null);
        items.Clear();
        oldModel.Source = null;
        SettleLayout(window);
        return previousRef;
    }

    private static string? GetTabContentText(TabControl tabControl)
    {
        var contentHost = tabControl
            .GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(presenter => presenter.Name == "PART_SelectedContentHost");
        return contentHost?.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault()?.Text;
    }

    private static Window CreateWindow(Control content, double width = 320, double height = 240)
    {
        return new Window
        {
            Width = width,
            Height = height,
            WindowDecorations = WindowDecorations.None,
            Content = content
        };
    }

    private static FuncDataTemplate<TestItem?> CreateTextBlockTemplate()
    {
        return new FuncDataTemplate<TestItem?>((item, _) => new TextBlock { Text = item?.Label });
    }

    private static FuncDataTemplate<TestItem?> CreateToggleSwitchTemplate()
    {
        return new FuncDataTemplate<TestItem?>((item, _) => new ToggleSwitch { Content = item?.Label });
    }

    private static FuncDataTemplate<TestItem?> CreateFixedHeightTemplate(double height)
    {
        return new FuncDataTemplate<TestItem?>((item, _) => new Border
        {
            Height = height,
            Child = new TextBlock { Text = item?.Label }
        });
    }

    private static TextBlock GetSingleTextBlock(Visual root)
    {
        return root.GetVisualDescendants().OfType<TextBlock>().Single();
    }

    private static void SettleLayout(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void CollectAndWait()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class TestTabControl : TabControl
    {
        protected override Type StyleKeyOverride => typeof(TabControl);

        public ISelectionModel SelectionModel
        {
            get => Selection;
            set => Selection = value;
        }

        public new SelectionMode SelectionMode
        {
            get => base.SelectionMode;
            set => base.SelectionMode = value;
        }
    }

    private sealed class TestItem(string label)
    {
        public string Label { get; } = label;
    }
}
