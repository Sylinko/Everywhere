using System.Collections;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Everywhere.Automation.TestApp;
using Everywhere.Automation.Testing;
using AvaloniaButton = Avalonia.Controls.Button;
using AvaloniaCheckBox = Avalonia.Controls.CheckBox;
using AvaloniaComboBox = Avalonia.Controls.ComboBox;
using AvaloniaHyperlinkButton = Avalonia.Controls.HyperlinkButton;
using AvaloniaMenu = Avalonia.Controls.Menu;
using AvaloniaMenuItem = Avalonia.Controls.MenuItem;
using AvaloniaProgressBar = Avalonia.Controls.ProgressBar;
using AvaloniaRadioButton = Avalonia.Controls.RadioButton;
using AvaloniaSeparator = Avalonia.Controls.Separator;
using AvaloniaListBox = Avalonia.Controls.ListBox;
using AvaloniaSlider = Avalonia.Controls.Slider;
using AvaloniaTabControl = Avalonia.Controls.TabControl;
using AvaloniaTabItem = Avalonia.Controls.TabItem;
using AvaloniaTextBox = Avalonia.Controls.TextBox;
using AvaloniaWindow = Avalonia.Controls.Window;
using ProgressBar = Everywhere.Automation.Testing.ProgressBar;

namespace Everywhere.Automation.Avalonia.TestApp;

internal sealed class AvaloniaScenarioController
{
    public AvaloniaWindow MainWindow => _windows[0];

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly TestAppOptions _options;
    private readonly GeneratedVisualScenario _scenario;
    private readonly TestAppControlChannel _channel = new();
    private readonly ManualResetEventSlim _resumeUiThread = new(initialState: true);
    private readonly List<AvaloniaWindow> _windows = [];
    private readonly List<TestAppAnchorStatus> _anchors = [];
    private long _step;
    private long _revision;
    private int _openedRoots;
    private int _closedRoots;

    public AvaloniaScenarioController(IClassicDesktopStyleApplicationLifetime desktop, TestAppOptions options)
    {
        _desktop = desktop;
        _options = options;
        _scenario = new VisualScenarioGenerator().Generate(options.ResolveScenario(), options.Seed);

        for (var i = 0; i < _scenario.Roots.Count; i++)
        {
            var rootIndex = i;
            var window = new AvaloniaWindow
            {
                Title = $"Everywhere Visual Context TestApp — {_scenario.Name} — Root {rootIndex}",
                Width = 1000,
                Height = 720,
                Position = new PixelPoint(80 + rootIndex * 48, 80 + rootIndex * 48),
            };
            window.Opened += (_, _) => OnRootOpened();
            window.Closed += (_, _) => OnRootClosed();
            _windows.Add(window);
            RebuildRoot(rootIndex);
        }

        _channel.CommandReceived += OnCommandReceived;
        _channel.ProtocolError += exception =>
            Dispatcher.UIThread.Post(() => Publish(TestAppStatusKind.Error, exception.Message));
    }

    public void Start()
    {
        _channel.Start();
        foreach (var window in _windows)
        {
            window.Show();
        }
    }

    private void RebuildRoot(int rootIndex)
    {
        _anchors.RemoveAll(anchor => anchor.RootIndex == rootIndex);
        var root = Resolve(_scenario.Roots[rootIndex]);
        var content = root.Kind is ScenarioControlKind.Window or ScenarioControlKind.Dialog
            ? CreateContainer(root, rootIndex.ToString())
            : CreateControl(root, rootIndex.ToString());
        _windows[rootIndex].Content = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    private Control CreateControl(VisualControl declaration, string path)
    {
        var control = Resolve(declaration);
        Control result = control.Kind switch
        {
            ScenarioControlKind.Text => new TextBlock { Text = control.TextContent, TextWrapping = TextWrapping.Wrap },
            ScenarioControlKind.Document => new AvaloniaTextBox
            {
                Text = control.TextContent,
                AcceptsReturn = true,
                IsReadOnly = (control.States & ScenarioControlStates.ReadOnly) != 0,
                TextWrapping = TextWrapping.NoWrap,
                Width = 850,
                Height = 420,
            },
            ScenarioControlKind.Image => new Border
            {
                Width = 96,
                Height = 64,
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = control.Name },
            },
            ScenarioControlKind.Button => new AvaloniaButton { Content = control.Name },
            ScenarioControlKind.Link => new AvaloniaHyperlinkButton { Content = control.Name },
            ScenarioControlKind.TextBox => new AvaloniaTextBox { Text = control.TextContent, Width = 360 },
            ScenarioControlKind.CheckBox => new AvaloniaCheckBox { Content = control.Name },
            ScenarioControlKind.RadioButton => new AvaloniaRadioButton { Content = control.Name },
            ScenarioControlKind.ComboBox => CreateComboBox(control),
            ScenarioControlKind.Slider => new AvaloniaSlider { Width = 240 },
            ScenarioControlKind.ProgressBar => new AvaloniaProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = ((ProgressBar)control).Value,
                Width = 240,
            },
            ScenarioControlKind.VirtualList => CreateVirtualList(control, path),
            ScenarioControlKind.MenuBar => CreateMenuBar(control, path),
            ScenarioControlKind.TabControl => CreateTabControl(control, path),
            ScenarioControlKind.Separator => new AvaloniaSeparator { Width = 240 },
            _ => CreateContainer(control, path),
        };

        var nativeId = GetNativeId(control, path);
        AutomationProperties.SetAutomationId(result, nativeId);
        result.IsEnabled = (control.States & ScenarioControlStates.Disabled) == 0;
        if (control.IsCore)
        {
            _anchors.Add(new TestAppAnchorStatus(GetRootIndex(path), path, control.Key, nativeId));
        }

        return result;
    }

    private Control CreateContainer(VisualControl control, string path)
    {
        var panel = new StackPanel
        {
            Orientation = control.Kind == ScenarioControlKind.HorizontalStack
                ? Orientation.Horizontal
                : Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(6),
        };

        for (var i = 0; i < control.ChildCount; i++)
        {
            panel.Children.Add(CreateControl(control.GetChild(i), $"{path}/{i}"));
        }

        return panel;
    }

    private AvaloniaComboBox CreateComboBox(VisualControl control)
    {
        var items = new string[control.ChildCount];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = GetDisplayText(Resolve(control.GetChild(i)));
        }

        return new AvaloniaComboBox
        {
            ItemsSource = items,
            SelectedIndex = items.Length == 0 ? -1 : 0,
            Width = 240,
        };
    }

    private AvaloniaListBox CreateVirtualList(VisualControl control, string path) => new()
    {
        ItemsSource = new ScenarioItemList(control, Resolve),
        ItemTemplate = new FuncDataTemplate(
            typeof(ScenarioListItem),
            (item, _) => item is ScenarioListItem scenarioItem
                ? CreateControl(scenarioItem.Control, $"{path}/{scenarioItem.Index}")
                : null,
            // Avalonia clears Content before ContentTemplate when recycling a ListBox container,
            // so the explicitly assigned template is briefly invoked with null content.
            // The generated controls embed the current item directly and cannot be safely rebound.
            supportsRecycling: false),
        Width = 850,
        Height = 480,
    };

    private AvaloniaMenu CreateMenuBar(VisualControl control, string path)
    {
        var items = new object[control.ChildCount];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = CreateMenuEntry(Resolve(control.GetChild(i)), $"{path}/{i}");
        }

        return new AvaloniaMenu { ItemsSource = items };
    }

    private Control CreateMenuEntry(VisualControl control, string path)
    {
        Control result;
        if (control.Kind == ScenarioControlKind.Separator)
        {
            result = new AvaloniaSeparator();
        }
        else
        {
            var children = new object[control.ChildCount];
            for (var i = 0; i < children.Length; i++)
            {
                children[i] = CreateMenuEntry(Resolve(control.GetChild(i)), $"{path}/{i}");
            }

            result = new AvaloniaMenuItem
            {
                Header = control.Name,
                ItemsSource = children,
            };
        }

        var nativeId = GetNativeId(control, path);
        AutomationProperties.SetAutomationId(result, nativeId);
        result.IsEnabled = (control.States & ScenarioControlStates.Disabled) == 0;
        if (control.IsCore)
        {
            _anchors.Add(new TestAppAnchorStatus(GetRootIndex(path), path, control.Key, nativeId));
        }

        return result;
    }

    private AvaloniaTabControl CreateTabControl(VisualControl control, string path)
    {
        var items = new AvaloniaTabItem[control.ChildCount];
        for (var i = 0; i < items.Length; i++)
        {
            var child = Resolve(control.GetChild(i));
            var childPath = $"{path}/{i}";
            var nativeId = GetNativeId(child, childPath);
            var item = new AvaloniaTabItem
            {
                Header = child.Name,
                Content = CreateContainer(child, childPath),
                IsEnabled = (child.States & ScenarioControlStates.Disabled) == 0,
            };
            AutomationProperties.SetAutomationId(item, nativeId);
            items[i] = item;
            if (child.IsCore)
            {
                _anchors.Add(new TestAppAnchorStatus(GetRootIndex(path), childPath, child.Key, nativeId));
            }
        }

        return new AvaloniaTabControl
        {
            ItemsSource = items,
            Width = 850,
            Height = 520,
        };
    }

    private VisualControl Resolve(VisualControl control)
    {
        while (control is OnMoveNext mutation)
        {
            control = mutation.Resolve(_step);
        }

        return control;
    }

    private static string GetDisplayText(VisualControl control) =>
        control.Name ?? control.TextContent ?? control.Kind.ToString();

    private static string GetNativeId(VisualControl control, string path) =>
        control.Key ?? $"vc-{path.Replace('/', '-')}";

    private static int GetRootIndex(string path)
    {
        var separator = path.IndexOf('/');
        return int.Parse(separator < 0 ? path : path[..separator]);
    }

    private void OnRootOpened()
    {
        _openedRoots++;
        if (_openedRoots == _windows.Count)
        {
            Publish(TestAppStatusKind.Ready);
        }
    }

    private void OnRootClosed()
    {
        _closedRoots++;
        if (_closedRoots == _windows.Count)
        {
            _desktop.Shutdown();
        }
    }

    private void OnCommandReceived(TestAppCommand command)
    {
        if (command.Kind == TestAppCommandKind.ResumeUiThread)
        {
            _resumeUiThread.Set();
            return;
        }

        if (command.Kind == TestAppCommandKind.Stop)
        {
            _resumeUiThread.Set();
        }

        Dispatcher.UIThread.Post(() => ExecuteCommand(command));
    }

    private void ExecuteCommand(TestAppCommand command)
    {
        switch (command.Kind)
        {
            case TestAppCommandKind.MoveNext:
                _step++;
                _revision++;
                for (var i = 0; i < _windows.Count; i++)
                {
                    RebuildRoot(i);
                }

                Publish(TestAppStatusKind.Advanced);
                break;
            case TestAppCommandKind.SuspendUiThread:
                _resumeUiThread.Reset();
                Publish(TestAppStatusKind.UiThreadSuspended);
                _resumeUiThread.Wait();
                Publish(TestAppStatusKind.UiThreadResumed);
                break;
            case TestAppCommandKind.Navigate:
                Publish(TestAppStatusKind.Error, "Navigate is supported only by the CefSharp TestApp.");
                break;
            case TestAppCommandKind.Stop:
                foreach (var window in _windows)
                {
                    window.Close();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null);
        }
    }

    private void Publish(TestAppStatusKind kind, string? error = null)
    {
        var roots = new TestAppRootStatus[_windows.Count];
        for (var i = 0; i < roots.Length; i++)
        {
            roots[i] = new TestAppRootStatus(i, _windows[i].TryGetPlatformHandle()?.Handle.ToInt64() ?? 0);
        }

        _channel.Publish(new TestAppStatus(
            kind,
            _options.Scenario,
            _options.Seed,
            _step,
            _revision,
            Environment.ProcessId,
            roots,
            [.. _anchors],
            error));
    }

    private sealed class ScenarioItemList(VisualControl control, Func<VisualControl, VisualControl> resolve) : IList
    {
        public int Count => control.ChildCount;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public object? this[int index]
        {
            get => new ScenarioListItem(index, resolve(control.GetChild(index)));
            set => throw new NotSupportedException();
        }

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value) => false;

        public int IndexOf(object? value) => -1;

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index)
        {
            for (var i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }

        public IEnumerator GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }
    }

    /// <summary>
    /// Carries the logical index needed by the item template without contributing a duplicate
    /// container-type name to the accessibility tree.
    /// </summary>
    private sealed record ScenarioListItem(int Index, VisualControl Control)
    {
        /// <inheritdoc />
        public override string ToString() => string.Empty;
    }
}
