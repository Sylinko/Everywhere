using Everywhere.Automation.TestApp;
using Everywhere.Automation.Testing;
using ProgressBar = Everywhere.Automation.Testing.ProgressBar;
using WinFormsButton = System.Windows.Forms.Button;
using WinFormsCheckBox = System.Windows.Forms.CheckBox;
using WinFormsComboBox = System.Windows.Forms.ComboBox;
using WinFormsProgressBar = System.Windows.Forms.ProgressBar;
using WinFormsRadioButton = System.Windows.Forms.RadioButton;
using WinFormsTabControl = System.Windows.Forms.TabControl;
using WinFormsTextBox = System.Windows.Forms.TextBox;

namespace Everywhere.Automation.WinForms.TestApp;

internal sealed class WinFormsScenarioApplicationContext : ApplicationContext
{
    private readonly TestAppOptions _options;
    private readonly GeneratedVisualScenario _scenario;
    private readonly TestAppControlChannel _channel = new();
    private readonly ManualResetEventSlim _resumeUiThread = new(initialState: true);
    private readonly List<Form> _forms = [];
    private readonly List<TestAppAnchorStatus> _anchors = [];
    private long _step;
    private long _revision;
    private int _shownRoots;
    private int _closedRoots;

    public WinFormsScenarioApplicationContext(TestAppOptions options)
    {
        _options = options;
        _scenario = new VisualScenarioGenerator().Generate(options.ResolveScenario(), options.Seed);
        CreateRootForms();

        _channel.CommandReceived += OnCommandReceived;
        _channel.ProtocolError += OnProtocolError;
        _channel.Start();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _resumeUiThread.Set();
            _channel.CommandReceived -= OnCommandReceived;
            _channel.ProtocolError -= OnProtocolError;
            foreach (var form in _forms)
            {
                form.Dispose();
            }

            _resumeUiThread.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CreateRootForms()
    {
        for (var i = 0; i < _scenario.Roots.Count; i++)
        {
            var rootIndex = i;
            var form = new Form
            {
                Name = $"ScenarioRoot{rootIndex}",
                Text = $"Everywhere Visual Context TestApp — {_scenario.Name} — Root {rootIndex}",
                Width = 1000,
                Height = 720,
                StartPosition = FormStartPosition.Manual,
                Left = 80 + rootIndex * 48,
                Top = 80 + rootIndex * 48,
            };
            form.Shown += (_, _) => OnRootShown();
            form.FormClosed += (_, _) => OnRootClosed();
            _forms.Add(form);
            RebuildRoot(rootIndex);
            form.Show();
        }
    }

    private void RebuildRoot(int rootIndex)
    {
        _anchors.RemoveAll(anchor => anchor.RootIndex == rootIndex);
        var form = _forms[rootIndex];
        form.SuspendLayout();
        while (form.Controls.Count > 0)
        {
            form.Controls[0].Dispose();
        }

        var root = Resolve(_scenario.Roots[rootIndex]);
        var content = root.Kind is ScenarioControlKind.Window or ScenarioControlKind.Dialog
            ? CreateContainer(root, rootIndex.ToString())
            : CreateControl(root, rootIndex.ToString());
        content.Dock = DockStyle.Fill;
        form.Controls.Add(content);
        form.ResumeLayout(performLayout: true);
    }

    private Control CreateControl(VisualControl declaration, string path)
    {
        var control = Resolve(declaration);
        Control result = control.Kind switch
        {
            ScenarioControlKind.Text => new Label { AutoSize = true, Text = control.TextContent },
            ScenarioControlKind.Document => new WinFormsTextBox
            {
                Multiline = true,
                ReadOnly = (control.States & ScenarioControlStates.ReadOnly) != 0,
                ScrollBars = ScrollBars.Both,
                Text = control.TextContent,
                Width = 850,
                Height = 420,
            },
            ScenarioControlKind.Image => new PictureBox
            {
                AccessibleName = control.Name,
                Width = 96,
                Height = 64,
                BorderStyle = BorderStyle.FixedSingle,
            },
            ScenarioControlKind.Button => new WinFormsButton { AutoSize = true, Text = control.Name },
            ScenarioControlKind.Link => new LinkLabel { AutoSize = true, Text = control.Name },
            ScenarioControlKind.TextBox => CreateTextBox(control),
            ScenarioControlKind.CheckBox => new WinFormsCheckBox { AutoSize = true, Text = control.Name },
            ScenarioControlKind.RadioButton => new WinFormsRadioButton { AutoSize = true, Text = control.Name },
            ScenarioControlKind.ComboBox => CreateComboBox(control),
            ScenarioControlKind.Slider => new TrackBar { AccessibleName = control.Name, Width = 240 },
            ScenarioControlKind.ProgressBar => new WinFormsProgressBar
            {
                AccessibleName = control.Name,
                Minimum = 0,
                Maximum = 100,
                Value = ((ProgressBar)control).Value,
                Width = 240,
            },
            ScenarioControlKind.VirtualList => CreateVirtualGrid(control),
            ScenarioControlKind.Tree => CreateTree(control, path),
            ScenarioControlKind.MenuBar => CreateMenuBar(control, path),
            ScenarioControlKind.TabControl => CreateTabControl(control, path),
            ScenarioControlKind.Separator => new Label
            {
                AutoSize = false,
                BorderStyle = BorderStyle.Fixed3D,
                Height = 2,
                Width = 240,
            },
            _ => CreateContainer(control, path),
        };

        var nativeId = GetNativeId(control, path);
        result.Name = nativeId;
        result.AccessibleName ??= control.Name;
        result.Enabled = (control.States & ScenarioControlStates.Disabled) == 0;
        if (control.IsCore)
        {
            _anchors.Add(new TestAppAnchorStatus(GetRootIndex(path), path, control.Key, nativeId));
        }

        return result;
    }

    private Control CreateContainer(VisualControl control, string path)
    {
        var panel = new FlowLayoutPanel
        {
            AutoScroll = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = control.Kind == ScenarioControlKind.HorizontalStack
                ? FlowDirection.LeftToRight
                : FlowDirection.TopDown,
            WrapContents = control.Kind == ScenarioControlKind.HorizontalStack,
            Padding = new Padding(6),
        };

        for (var i = 0; i < control.ChildCount; i++)
        {
            panel.Controls.Add(CreateControl(control.GetChild(i), $"{path}/{i}"));
        }

        return panel;
    }

    private Control CreateVirtualGrid(VisualControl control)
    {
        var firstParts = control.ChildCount == 0
            ? Array.Empty<TestAppDisplayPart>()
            : TestAppItemProjection.CreateParts(control.GetChild(0), Resolve);
        var grid = new DataGridView
        {
            VirtualMode = true,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Width = 850,
            Height = 480,
        };
        if (firstParts.Count == 0)
        {
            grid.Columns.Add("Content", "Content");
        }
        else
        {
            for (var i = 0; i < firstParts.Count; i++)
            {
                grid.Columns.Add(CreateVirtualColumn(firstParts[i], i));
            }
        }

        grid.RowCount = control.ChildCount;
        var cachedRowIndex = -1;
        IReadOnlyList<TestAppDisplayPart> cachedParts = [];
        grid.CellValueNeeded += (_, args) =>
        {
            if (args.RowIndex >= 0 && args.RowIndex < control.ChildCount)
            {
                if (cachedRowIndex != args.RowIndex)
                {
                    cachedRowIndex = args.RowIndex;
                    cachedParts = TestAppItemProjection.CreateParts(control.GetChild(args.RowIndex), Resolve);
                }

                args.Value = args.ColumnIndex < cachedParts.Count
                    ? cachedParts[args.ColumnIndex].Text
                    : null;
            }
        };
        return grid;
    }

    private static DataGridViewColumn CreateVirtualColumn(TestAppDisplayPart part, int index)
    {
        DataGridViewColumn column = part.Kind switch
        {
            ScenarioControlKind.Button => new DataGridViewButtonColumn(),
            ScenarioControlKind.Link => new DataGridViewLinkColumn(),
            _ => new DataGridViewTextBoxColumn(),
        };
        column.Name = $"Part{index}";
        column.HeaderText = part.Header;
        return column;
    }

    private WinFormsComboBox CreateComboBox(VisualControl control)
    {
        var comboBox = new WinFormsComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        for (var i = 0; i < control.ChildCount; i++)
        {
            comboBox.Items.Add(GetDisplayText(Resolve(control.GetChild(i))));
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }

        return comboBox;
    }

    private WinFormsTextBox CreateTextBox(VisualControl control) => control.Key == "input"
        ? new ProviderProbeTextBox(_resumeUiThread) { Text = control.TextContent, Width = 360 }
        : new WinFormsTextBox { Text = control.TextContent, Width = 360 };

    private TreeView CreateTree(VisualControl control, string path)
    {
        var tree = new TreeView { Width = 300, Height = 480 };
        for (var i = 0; i < control.ChildCount; i++)
        {
            tree.Nodes.Add(CreateTreeNode(Resolve(control.GetChild(i)), $"{path}/{i}"));
        }

        return tree;
    }

    private TreeNode CreateTreeNode(VisualControl control, string path)
    {
        var nativeId = GetNativeId(control, path);
        var node = new TreeNode(GetDisplayText(control)) { Name = nativeId };
        if (control.IsCore)
        {
            _anchors.Add(new TestAppAnchorStatus(GetRootIndex(path), path, control.Key, nativeId));
        }

        for (var i = 0; i < control.ChildCount; i++)
        {
            node.Nodes.Add(CreateTreeNode(Resolve(control.GetChild(i)), $"{path}/{i}"));
        }

        return node;
    }

    private MenuStrip CreateMenuBar(VisualControl control, string path)
    {
        var menu = new MenuStrip();
        for (var i = 0; i < control.ChildCount; i++)
        {
            var child = Resolve(control.GetChild(i));
            menu.Items.Add(CreateMenuEntry(child, $"{path}/{i}"));
        }

        return menu;
    }

    private ToolStripItem CreateMenuEntry(VisualControl control, string path)
    {
        var nativeId = GetNativeId(control, path);
        if (control.Kind == ScenarioControlKind.Separator)
        {
            return new ToolStripSeparator { Name = nativeId };
        }

        var item = new ToolStripMenuItem(GetDisplayText(control))
        {
            Name = nativeId,
            Enabled = (control.States & ScenarioControlStates.Disabled) == 0,
        };
        if (control.IsCore)
        {
            _anchors.Add(new TestAppAnchorStatus(GetRootIndex(path), path, control.Key, nativeId));
        }

        for (var i = 0; i < control.ChildCount; i++)
        {
            item.DropDownItems.Add(CreateMenuEntry(Resolve(control.GetChild(i)), $"{path}/{i}"));
        }

        return item;
    }

    private WinFormsTabControl CreateTabControl(VisualControl control, string path)
    {
        var tabControl = new WinFormsTabControl { Width = 850, Height = 520 };
        for (var i = 0; i < control.ChildCount; i++)
        {
            var child = Resolve(control.GetChild(i));
            var childPath = $"{path}/{i}";
            var nativeId = GetNativeId(child, childPath);
            var page = new TabPage(GetDisplayText(child)) { Name = nativeId };
            var content = CreateContainer(child, childPath);
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            tabControl.TabPages.Add(page);
            if (child.IsCore)
            {
                _anchors.Add(new TestAppAnchorStatus(GetRootIndex(path), childPath, child.Key, nativeId));
            }
        }

        return tabControl;
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

    private void OnRootShown()
    {
        _shownRoots++;
        if (_shownRoots == _forms.Count)
        {
            Publish(TestAppStatusKind.Ready);
        }
    }

    private void OnRootClosed()
    {
        _closedRoots++;
        if (_closedRoots == _forms.Count)
        {
            ExitThread();
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

        var dispatcher = _forms[0];
        if (!dispatcher.IsHandleCreated || dispatcher.IsDisposed)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ExecuteCommand(command));
    }

    private void ExecuteCommand(TestAppCommand command)
    {
        switch (command.Kind)
        {
            case TestAppCommandKind.MoveNext:
                _step++;
                _revision++;
                for (var i = 0; i < _forms.Count; i++)
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
                foreach (var form in _forms)
                {
                    form.Close();
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null);
        }
    }

    private void OnProtocolError(Exception exception) =>
        Publish(TestAppStatusKind.Error, exception.Message);

    private void Publish(TestAppStatusKind kind, string? error = null)
    {
        var roots = new TestAppRootStatus[_forms.Count];
        for (var i = 0; i < roots.Length; i++)
        {
            roots[i] = new TestAppRootStatus(i, _forms[i].Handle.ToInt64());
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

    private sealed class ProviderProbeTextBox(ManualResetEventSlim providerGate) : WinFormsTextBox
    {
        protected override AccessibleObject CreateAccessibilityInstance() => new ProviderProbeAccessibleObject(this, providerGate);

        private sealed class ProviderProbeAccessibleObject(ProviderProbeTextBox owner, ManualResetEventSlim providerGate) : ControlAccessibleObject(owner)
        {
            public override string? Name
            {
                get
                {
                    providerGate.Wait();
                    return base.Name;
                }
                set => base.Name = value;
            }

            public override string? Value
            {
                get
                {
                    providerGate.Wait();
                    return owner.Text;
                }
                set => owner.Text = value;
            }

            public override AccessibleRole Role => AccessibleRole.Text;
        }
    }
}
