using System.Diagnostics;
using System.Diagnostics.Metrics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ShadUI;
#if DEBUG
using ClassicDiagnostics.Avalonia;
#endif

namespace Everywhere;

public sealed partial class App(IServiceProvider serviceProvider) : Application, IRecipient<ApplicationMessage>, IOobeHost
{
    public static string Version => RuntimeConstants.Version.ToString();

    public static IClipboard Clipboard =>
        _topLevel?.Clipboard ?? throw new InvalidOperationException("Clipboard is not available.");

    public static IStorageProvider StorageProvider =>
        _topLevel?.StorageProvider ?? throw new InvalidOperationException("StorageProvider is not available.");

    public static ILauncher Launcher => BetterBclLauncher.Shared;

    public static Screens Screens => _topLevel?.Screens ?? throw new InvalidOperationException("Screens is not available.");

    public static ThemeManager ThemeManager => _themeManager ?? throw new InvalidOperationException("Application is not initialized.");

    private static TopLevel? _topLevel;
    private static ThemeManager? _themeManager;

    private readonly Dictionary<Type, TransientWindow> _transientWindows = new();
    private readonly IKeyValueStorage _keyValueStorage = serviceProvider.GetRequiredService<IKeyValueStorage>();
    private readonly INativeHelper _nativeHelper = serviceProvider.GetRequiredService<INativeHelper>();
    private readonly IWindowHelper _windowHelper = serviceProvider.GetRequiredService<IWindowHelper>();
    private readonly OobeCoordinator? _oobeCoordinator = serviceProvider.GetService<OobeCoordinator>();

    // Native message boxes run a nested Windows message loop. A dispatcher exception can therefore
    // arrive while the first error dialog is still open; without this guard every nested exception
    // would open another modal box and eventually overflow the process stack.
    private static int _isShowingDispatcherException;

    /// <summary>
    /// Flag to prevent multiple calls to ShowWindow method from event loop.
    /// </summary>
    private bool _isShowWindowBusy;

    public override void Initialize()
    {
        InitializeErrorHandler();

        AvaloniaXamlLoader.Load(this);

        _topLevel = new Window() ?? throw new InvalidOperationException("Application is not initialized correctly.");

#if DEBUG
        if (Design.IsDesignMode)
        {
            return;
        }

        this.AttachDevTools();
#endif

        // After this, ThemeChanged event from the system can be received
        _themeManager = new ThemeManager(this);
        AppearanceManager.Shared.Attach(this);

        // Register to receive application commands
        // e.g. ShowMainWindow
        WeakReferenceMessenger.Default.Register(this);
        _oobeCoordinator?.AttachHost(this);

        InitializeMarkdown();
    }

    private void InitializeErrorHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // Mark the dispatcher event handled before doing any logging or UI work. MessageBoxW is
            // synchronous and pumps messages, so leaving this until after Show would let the same
            // failing dispatcher operation re-enter this handler.
            e.Handled = true;

            if (Interlocked.Exchange(ref _isShowingDispatcherException, 1) != 0)
            {
                // The first dialog owns the user-facing notification. Nested failures are handled
                // silently; attempting to log or display each one would only amplify the modal-loop
                // recursion that caused the original crash.
                return;
            }

            try
            {
                try
                {
                    Log.Logger.Error(e.Exception, "UI Thread Unhandled Exception");
                }
                catch (Exception loggingException)
                {
                    // Error reporting must never become a second unhandled exception.
                    Debug.WriteLine(loggingException);
                }

                try
                {
                    NativeMessageBox.Show(
                        "Unexpected Error",
                        $"An unexpected error occurred:\n{e.Exception.Message}\n\nPlease check the logs for more details.",
                        NativeMessageBoxButtons.Ok,
                        NativeMessageBoxIcon.Error);
                }
                catch (Exception messageBoxException)
                {
                    // A platform message-box failure should not re-enter the dispatcher exception
                    // path. The original exception has already been marked handled and logged.
                    Debug.WriteLine(messageBoxException);
                }
            }
            finally
            {
                Volatile.Write(ref _isShowingDispatcherException, 0);
            }
        };

        serviceProvider.GetService<HostProcessCoordinator>()?.AutomaticRecoveryStopped += e =>
        {
            Dispatcher.UIThread.PostOnDemand(() =>
            {
                var messageTemplate = e.Role switch
                {
                    ProcessRole.Input => LocaleKey.ProcessIsolation_InputHostRecoveryStopped_NotificationMessage.I18N(),
                    ProcessRole.Automation => LocaleKey.ProcessIsolation_AutomationHostRecoveryStopped_NotificationMessage.I18N(),
                    _ => null
                };

                if (messageTemplate is null)
                {
                    Log.Logger.Warning("Automatic recovery stopped notification ignored for unsupported process role {ProcessRole}.", e.Role);
                    return;
                }

                var message = messageTemplate.Format(e.FailureCount, (int)e.FailureWindow.TotalMinutes);
                _nativeHelper.ShowDesktopNotificationAsync(message, LocaleKey.Common_Warning.I18N()).Detach(Log.Logger.ToExceptionHandler());
            });
        };
    }

    private static void InitializeMarkdown()
    {
        AsyncImageLoader.DefaultDecoders =
        [
            SvgImageDecoder.Shared,
            DefaultBitmapDecoder.Shared
        ];

        FileBasedAsyncImageLoaderCache.CacheDirectory = RuntimeConstants.EnsureCacheFolderPath("img");
        AsyncImageLoader.DefaultCache = FileBasedAsyncImageLoaderCache.Shared;

        MarkdownNode.Register<MathInlineNode>();
        MarkdownNode.Register<MathBlockNode>();

        MarkdownRenderer.ConfigurePipeline += x => x.UseMermaid().UseExtendedMathematics();
        MarkdownNode.Register<MermaidBlockNode>();
    }

    private static void RecordAppLaunchMetric()
    {
        const string OsType =
#if WINDOWS
            "Windows";
#elif LINUX
                "Linux";
#elif MACOS
                "macOS";
#else
                "Unknown";
#endif

        using var meter = new Meter(typeof(App).FullName.NotNull(), Version);
        meter.CreateCounter<int>("app.launches").Add(
            1,
            new TagList
            {
                { "os.type", OsType },
                { "os.description", RuntimeInformation.OSDescription },
                { "app.version", Version }
            });
    }

    /// <summary>
    /// Show the main window if it was not shown before or the version has changed.
    /// </summary>
    private void ShowMainWindowOnNeeded()
    {
        var currentVersion = RuntimeConstants.Version;
        var persistentState = serviceProvider.GetRequiredService<PersistentState>();
        if (!SemanticVersion.TryParse(persistentState.PreviousLaunchVersion, out var previousVersion))
        {
            previousVersion = new SemanticVersion(0);
        }

        _oobeCoordinator?.AttachHost(this);
        if (_oobeCoordinator?.TryShowPendingStartup() != true &&
            (Environment.GetCommandLineArgs().Contains("--ui") || previousVersion < currentVersion))
        {
            ShowMainWindow();
        }

        persistentState.PreviousLaunchVersion = currentVersion.ToString();
    }

    private void ShowWindow<TContent>() where TContent : Control
    {
        if (typeof(TContent) == typeof(MainView))
        {
            _oobeCoordinator?.AttachHost(this);
            if (_oobeCoordinator?.TryShowPendingStartup() == true) return;
        }

        if (_isShowWindowBusy) return;

        try
        {
            _isShowWindowBusy = true;

            var windowType = typeof(TContent);
            if (_transientWindows.TryGetValue(windowType, out var window))
            {
                if (window is { Content: not null })
                {
                    FocusWindow(window);
                    return;
                }

                window.Close();
            }

            var content = serviceProvider.GetRequiredService<TContent>();
            content.To<ISetLogicalParent>().SetParent(null);
            var createdWindow = new TransientWindow
            {
                Content = content
            };
            _transientWindows[windowType] = createdWindow;
            createdWindow.Closed += (_, _) => RemoveIfCurrent();

            try
            {
                WindowPlacementAssist.Attach(createdWindow, _keyValueStorage, windowType.FullName ?? windowType.Name);
                _windowHelper.InitializeWindow(createdWindow);
                createdWindow.Show();
            }
            catch
            {
                RemoveIfCurrent();

                try
                {
                    createdWindow.Close();
                }
                catch
                {
                    // Preserve the original window initialization/show error.
                }

                throw;
            }

            void RemoveIfCurrent()
            {
                if (_transientWindows.TryGetValue(windowType, out var current) && ReferenceEquals(current, createdWindow))
                {
                    _transientWindows.Remove(windowType);
                }
            }
        }
        finally
        {
            _isShowWindowBusy = false;
        }
    }

    public void ShowMainWindow() => ShowWindow<MainView>();

    public void ShowDebugWindow() => ShowWindow<VisualTreeDebugger>();

    bool IOobeHost.IsShuttingDown => _lifetime.IsCancellationRequested;

    bool IOobeHost.TryHideMainWindow()
    {
        if (!_transientWindows.TryGetValue(typeof(MainView), out var mainWindow) || !mainWindow.IsVisible)
        {
            return false;
        }

        mainWindow.Hide();
        return true;
    }

    private static void FocusWindow(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        var isTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = isTopmost;
        window.Activate();
    }

    void IRecipient<ApplicationMessage>.Receive(ApplicationMessage message)
    {
        if (_lifetime.IsCancellationRequested) return;

        switch (message)
        {
            case ShowTerminalUpdateGuidanceMessage:
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (_lifetime.IsCancellationRequested) return;

                    ShowMainWindow();
                    var mainView = serviceProvider.GetRequiredService<MainView>();
                    TerminalUpdateGuidance.ShowAsync(TopLevel.GetTopLevel(mainView)).Detach();
                });
                return;
            }
            case ShowWindowMessage { Name: ShowWindowMessage.MainWindow } showWindowMessage:
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (_lifetime.IsCancellationRequested) return;

                    ShowWindow<MainView>();
                    if (_oobeCoordinator?.IsActive != true && showWindowMessage.Route is not null)
                    {
                        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(showWindowMessage.Route));
                    }
                });
                break;
            }
        }
    }
}