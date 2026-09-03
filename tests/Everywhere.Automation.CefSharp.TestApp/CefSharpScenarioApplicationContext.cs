using System.Text.Json;
using CefSharp;
using CefSharp.WinForms;
using Everywhere.Automation.TestApp;
using Everywhere.Automation.Testing;
using ProgressBar = Everywhere.Automation.Testing.ProgressBar;

namespace Everywhere.Automation.CefSharp.TestApp;

internal sealed class CefSharpScenarioApplicationContext : ApplicationContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TestAppOptions _options;
    private readonly GeneratedVisualScenario? _scenario;
    private readonly string? _externalAddress;
    private readonly TestAppControlChannel _channel = new();
    private readonly ManualResetEventSlim _resumeUiThread = new(initialState: true);
    private readonly List<Form> _forms = [];
    private readonly List<ChromiumWebBrowser> _browsers = [];
    private readonly List<TestAppAnchorStatus> _anchors = [];
    private readonly bool[] _areRootsRendered;
    private long _step;
    private long _revision;
    private int _closedRoots;
    private volatile bool _isControlledNavigation;

    public CefSharpScenarioApplicationContext(TestAppOptions options, string? externalAddress = null)
    {
        _options = options;
        _externalAddress = externalAddress;
        _scenario = externalAddress is null ? new VisualScenarioGenerator().Generate(options.ResolveScenario(), options.Seed) : null;
        _areRootsRendered = new bool[_scenario?.Roots.Count ?? 1];
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
            foreach (var browser in _browsers)
            {
                browser.Dispose();
            }

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
        var pagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "index.html");
        var pageAddress = _externalAddress ?? new Uri(pagePath).AbsoluteUri;
        var rootCount = _scenario?.Roots.Count ?? 1;
        for (var i = 0; i < rootCount; i++)
        {
            var rootIndex = i;
            var browser = new ChromiumWebBrowser(pageAddress) { Dock = DockStyle.Fill };
            browser.LoadingStateChanged += (_, eventArgs) => OnLoadingStateChanged(rootIndex, eventArgs);
            browser.LoadError += (_, eventArgs) => OnLoadError(rootIndex, eventArgs);
            browser.JavascriptMessageReceived += (_, eventArgs) => OnJavascriptMessageReceived(rootIndex, eventArgs);

            var form = new Form
            {
                Name = $"ScenarioRoot{rootIndex}",
                Text = _scenario is null ? $"Everywhere Visual Context CefSharp Web Probe — {pageAddress}" : $"Everywhere Visual Context CefSharp TestApp — {_scenario.Name} — Root {rootIndex}",
                Width = 1000,
                Height = 720,
                StartPosition = FormStartPosition.Manual,
                Left = 80 + rootIndex * 48,
                Top = 80 + rootIndex * 48,
            };
            form.Controls.Add(browser);
            form.FormClosed += (_, _) => OnRootClosed();
            _browsers.Add(browser);
            _forms.Add(form);
            form.Show();
        }
    }

    private void OnLoadingStateChanged(int rootIndex, LoadingStateChangedEventArgs eventArgs)
    {
        if (eventArgs.IsLoading || _areRootsRendered[rootIndex])
        {
            return;
        }

        var form = _forms[rootIndex];
        if (form.IsHandleCreated && !form.IsDisposed)
        {
            form.BeginInvoke(() => CompleteInitialLoad(rootIndex));
        }
    }

    private void OnLoadError(int rootIndex, LoadErrorEventArgs eventArgs)
    {
        if (_externalAddress is null || _isControlledNavigation || !eventArgs.Frame.IsMain || eventArgs.ErrorCode == CefErrorCode.Aborted) return;
        var form = _forms[rootIndex];
        if (form.IsHandleCreated && !form.IsDisposed)
        {
            var error = $"Failed to load '{eventArgs.FailedUrl}': {eventArgs.ErrorText} ({eventArgs.ErrorCode}).";
            form.BeginInvoke(() => CompleteLoadError(rootIndex, error));
        }
    }

    private void CompleteLoadError(int rootIndex, string error)
    {
        if (_areRootsRendered[rootIndex]) return;
        _areRootsRendered[rootIndex] = true;
        Publish(TestAppStatusKind.Error, error);
    }

    private void CompleteInitialLoad(int rootIndex)
    {
        if (_areRootsRendered[rootIndex]) return;
        if (_externalAddress is null)
        {
            RenderInitialRoot(rootIndex);
            return;
        }

        _areRootsRendered[rootIndex] = true;
        if (_areRootsRendered.All(static isRendered => isRendered)) Publish(TestAppStatusKind.Ready);
    }

    private async void RenderInitialRoot(int rootIndex)
    {
        try
        {
            await RenderRootAsync(rootIndex).ConfigureAwait(true);
            _areRootsRendered[rootIndex] = true;
            if (_areRootsRendered.All(static isRendered => isRendered))
            {
                Publish(TestAppStatusKind.Ready);
            }
        }
        catch (Exception exception)
        {
            Publish(TestAppStatusKind.Error, exception.Message);
        }
    }

    private async Task RenderRootAsync(int rootIndex)
    {
        var scenario = _scenario ?? throw new InvalidOperationException("External webpage mode does not use declarative scenario rendering.");
        _anchors.RemoveAll(anchor => anchor.RootIndex == rootIndex);
        var root = CreateDto(Resolve(scenario.Roots[rootIndex]), rootIndex.ToString(), shouldCollectAnchors: true);
        var json = JsonSerializer.Serialize(root, JsonOptions);
        var response = await _browsers[rootIndex]
            .EvaluateScriptAsync($"globalThis.everywhere.render({json}, {_step});")
            .ConfigureAwait(true);
        if (!response.Success)
        {
            throw new InvalidOperationException($"CefSharp scenario rendering failed: {response.Message}");
        }
    }

    private BrowserControlDto CreateDto(VisualControl control, string path, bool shouldCollectAnchors)
    {
        control = Resolve(control);
        if (shouldCollectAnchors && control.IsCore)
        {
            _anchors.Add(new TestAppAnchorStatus(
                GetRootIndex(path),
                path,
                control.Key,
                GetNativeId(path)));
        }

        var isVirtual = control.Kind == ScenarioControlKind.VirtualList;
        var renderedChildCount = isVirtual ? Math.Min(40, control.ChildCount) : control.ChildCount;
        var children = new BrowserControlDto[renderedChildCount];
        for (var i = 0; i < children.Length; i++)
        {
            children[i] = CreateDto(control.GetChild(i), $"{path}/{i}", shouldCollectAnchors);
        }

        return new BrowserControlDto(
            path,
            control.Kind.ToString(),
            control.Key,
            control.Name,
            control.TextContent,
            control.States,
            control.IsCore,
            control.ChildCount,
            isVirtual,
            (control as ProgressBar)?.Value,
            children);
    }

    private void OnJavascriptMessageReceived(int rootIndex, JavascriptMessageReceivedEventArgs eventArgs)
    {
        if (eventArgs.Message is not string json)
        {
            return;
        }

        BrowserMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BrowserMessage>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            OnProtocolError(exception);
            return;
        }

        if (message is not { Kind: "virtualPage" })
        {
            return;
        }

        var form = _forms[rootIndex];
        if (form.IsHandleCreated && !form.IsDisposed)
        {
            form.BeginInvoke(() => RenderVirtualPage(rootIndex, message));
        }
    }

    private async void RenderVirtualPage(int rootIndex, BrowserMessage message)
    {
        try
        {
            var control = ResolvePath(rootIndex, message.Path);
            if (control.Kind != ScenarioControlKind.VirtualList)
            {
                return;
            }

            var start = Math.Clamp(message.Start, 0, Math.Max(0, control.ChildCount - 1));
            var count = Math.Clamp(message.Count, 1, 100);
            var end = Math.Min(control.ChildCount, start + count);
            var items = new BrowserControlDto[end - start];
            for (var i = start; i < end; i++)
            {
                items[i - start] = CreateDto(
                    control.GetChild(i),
                    $"{message.Path}/{i}",
                    shouldCollectAnchors: false);
            }

            var pathJson = JsonSerializer.Serialize(message.Path, JsonOptions);
            var itemsJson = JsonSerializer.Serialize(items, JsonOptions);
            var response = await _browsers[rootIndex]
                .EvaluateScriptAsync(
                    $"globalThis.everywhere.updateVirtualPage({pathJson}, {start}, {itemsJson}, {control.ChildCount});")
                .ConfigureAwait(true);
            if (!response.Success)
            {
                throw new InvalidOperationException($"CefSharp virtual page rendering failed: {response.Message}");
            }
        }
        catch (Exception exception)
        {
            Publish(TestAppStatusKind.Error, exception.Message);
        }
    }

    private VisualControl ResolvePath(int rootIndex, string path)
    {
        var scenario = _scenario ?? throw new InvalidOperationException("External webpage mode does not expose declarative controls.");
        var segments = path.Split('/');
        if (segments.Length == 0 || !int.TryParse(segments[0], out var pathRootIndex) || pathRootIndex != rootIndex)
        {
            throw new ArgumentException($"Invalid virtual control path '{path}'.", nameof(path));
        }

        var control = Resolve(scenario.Roots[rootIndex]);
        for (var i = 1; i < segments.Length; i++)
        {
            control = Resolve(control.GetChild(int.Parse(segments[i])));
        }

        return control;
    }

    private static string GetNativeId(string path) => $"vc-{path.Replace('/', '-')}";

    private static int GetRootIndex(string path)
    {
        var separator = path.IndexOf('/');
        return int.Parse(separator < 0 ? path : path[..separator]);
    }

    private VisualControl Resolve(VisualControl control)
    {
        while (control is OnMoveNext mutation)
        {
            control = mutation.Resolve(_step);
        }

        return control;
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
        if (dispatcher.IsHandleCreated && !dispatcher.IsDisposed)
        {
            dispatcher.BeginInvoke(() => ExecuteCommand(command));
        }
    }

    private async void ExecuteCommand(TestAppCommand command)
    {
        try
        {
            switch (command.Kind)
            {
                case TestAppCommandKind.MoveNext:
                    if (_externalAddress is not null) throw new InvalidOperationException("MoveNext is not defined for an external webpage probe.");
                    _step++;
                    _revision++;
                    for (var i = 0; i < _browsers.Count; i++)
                    {
                        await RenderRootAsync(i).ConfigureAwait(true);
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
                    await NavigateAsync(command.Address).ConfigureAwait(true);
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
        catch (Exception exception)
        {
            Publish(TestAppStatusKind.Error, exception.Message);
        }
    }

    private async Task NavigateAsync(string? address)
    {
        if (_externalAddress is null) throw new InvalidOperationException("Navigate is available only in the CefSharp external webpage mode.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Navigate requires an absolute HTTP or HTTPS address.", nameof(address));
        }

        _isControlledNavigation = true;
        try
        {
            var response = await _browsers[0].LoadUrlAsync(uri.AbsoluteUri).ConfigureAwait(true);
            if (response.ErrorCode != CefErrorCode.None || response.HttpStatusCode is < 200 or >= 400)
            {
                throw new InvalidOperationException($"Failed to navigate to '{uri.AbsoluteUri}': HTTP {response.HttpStatusCode}, {response.ErrorCode}.");
            }

            _revision++;
            _forms[0].Text = $"Everywhere Visual Context CefSharp Web Probe — {_browsers[0].Address}";
            Publish(TestAppStatusKind.Navigated);
        }
        finally
        {
            _isControlledNavigation = false;
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
            error,
            _externalAddress is null ? null : _browsers[0].Address));
    }

    private sealed record BrowserControlDto(
        string Path,
        string Kind,
        string? Key,
        string? Name,
        string? Text,
        ScenarioControlStates States,
        bool IsCore,
        int ChildCount,
        bool IsVirtual,
        int? ProgressValue,
        IReadOnlyList<BrowserControlDto> Children);

    private sealed record BrowserMessage(string Kind, string Path, int Start, int Count);
}
