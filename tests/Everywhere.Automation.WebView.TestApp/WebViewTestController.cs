using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Everywhere.Automation.TestApp;

namespace Everywhere.Automation.WebView.TestApp;

internal sealed class WebViewTestController
{
    public Window MainWindow { get; }

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly WebViewTestOptions _options;
    private readonly NativeWebView _webView = new();
    private readonly TestAppControlChannel _channel = new();
    private readonly ManualResetEventSlim _resumeUiThread = new(initialState: true);
    private Uri _address;
    private long _revision;
    private bool _isInitialNavigation = true;

    public WebViewTestController(IClassicDesktopStyleApplicationLifetime desktop, WebViewTestOptions options)
    {
        _desktop = desktop;
        _options = options;
        _address = options.Address;
        MainWindow = new Window
        {
            Title = $"Everywhere Automation WebView TestApp — {_address}",
            Width = 1000,
            Height = 720,
            Position = new PixelPoint(80, 80),
            Content = _webView,
        };
        MainWindow.Opened += OnWindowOpened;
        MainWindow.Closed += OnWindowClosed;
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _channel.CommandReceived += OnCommandReceived;
        _channel.ProtocolError += exception => Dispatcher.UIThread.Post(() => Publish(TestAppStatusKind.Error, exception.Message));
    }

    public void Start()
    {
        _channel.Start();
        MainWindow.Show();
    }

    private void OnWindowOpened(object? sender, EventArgs eventArgs) => _webView.Navigate(_address);

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        _resumeUiThread.Set();
        _webView.EnvironmentRequested -= OnEnvironmentRequested;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _channel.CommandReceived -= OnCommandReceived;
        _resumeUiThread.Dispose();
        _desktop.Shutdown();
    }

    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs eventArgs)
    {
        eventArgs.EnableDevTools = true;
        if (eventArgs is WindowsWebView2EnvironmentRequestedEventArgs windows) windows.AdditionalBrowserArguments = "--force-renderer-accessibility";
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess)
        {
            Publish(TestAppStatusKind.Error, $"Failed to navigate to '{_address}'.");
            return;
        }

        _address = _webView.Source ?? _address;
        MainWindow.Title = $"Everywhere Automation WebView TestApp — {_address}";
        if (_isInitialNavigation)
        {
            _isInitialNavigation = false;
            Publish(TestAppStatusKind.Ready);
            return;
        }

        _revision++;
        Publish(TestAppStatusKind.Navigated);
    }

    private void OnCommandReceived(TestAppCommand command)
    {
        if (command.Kind == TestAppCommandKind.ResumeUiThread)
        {
            _resumeUiThread.Set();
            return;
        }

        if (command.Kind == TestAppCommandKind.Stop) _resumeUiThread.Set();
        Dispatcher.UIThread.Post(() => ExecuteCommand(command));
    }

    private void ExecuteCommand(TestAppCommand command)
    {
        switch (command.Kind)
        {
            case TestAppCommandKind.Navigate:
                if (!WebViewTestOptions.TryParseAddress(command.Address, out var address))
                {
                    Publish(TestAppStatusKind.Error, "Navigate requires an absolute HTTP or HTTPS address.");
                    return;
                }

                _address = address;
                _webView.Navigate(address);
                break;
            case TestAppCommandKind.SuspendUiThread:
                _resumeUiThread.Reset();
                Publish(TestAppStatusKind.UiThreadSuspended);
                _resumeUiThread.Wait();
                Publish(TestAppStatusKind.UiThreadResumed);
                break;
            case TestAppCommandKind.ResumeUiThread:
                break;
            case TestAppCommandKind.Stop:
                MainWindow.Close();
                break;
            case TestAppCommandKind.MoveNext:
                Publish(TestAppStatusKind.Error, "MoveNext is not defined for the real-web TestApp.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null);
        }
    }

    private void Publish(TestAppStatusKind kind, string? error = null)
    {
        var root = new TestAppRootStatus(0, MainWindow.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0);
        _channel.Publish(new TestAppStatus(kind, _options.Common.Scenario, _options.Common.Seed, 0, _revision, Environment.ProcessId, [root], [], error, _address.AbsoluteUri));
    }
}
