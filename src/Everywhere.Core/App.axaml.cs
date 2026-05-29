using System.Diagnostics;
using System.Diagnostics.Metrics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AttachedProperties;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ShadUI;

namespace Everywhere;

public class App(IServiceProvider serviceProvider) : Application, IRecipient<ApplicationMessage>
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
            ServiceLocator.Build(x => x.AddAvaloniaBasicServices());
            return;
        }
#endif

        Window.WindowClosedEvent.AddClassHandler<TransientWindow>(HandleTransientWindowClosed);

        // After this, ThemeChanged event from the system can be received
        _themeManager = new ThemeManager(this);

        // Register to receive application commands
        // e.g. ShowMainWindow
        WeakReferenceMessenger.Default.Register(this);

        InitializeMarkdown();
        InitializeApp();

        TrayIcon.SetIcons(this, [new MainTrayIcon(this, serviceProvider)]);

        RecordAppLaunchMetric();
    }

    private void HandleTransientWindowClosed(TransientWindow sender, RoutedEventArgs args)
    {
        sender.Content = null;

        if (sender.Content is { } content)
        {
            _transientWindows.Remove(content.GetType());
            content.To<ISetLogicalParent>().SetParent(null);
        }
    }

    private static void InitializeErrorHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Logger.Error(e.Exception, "UI Thread Unhandled Exception");

            NativeMessageBox.Show(
                "Unexpected Error",
                $"An unexpected error occurred:\n{e.Exception.Message}\n\nPlease check the logs for more details.",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);

            e.Handled = true;
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

        MarkdownRenderer.ConfigurePipeline += x => x.UseMermaid();
        MarkdownNode.Register<MermaidBlockNode>();
    }

    private void InitializeApp()
    {
        try
        {
            foreach (var group in serviceProvider
                         .GetRequiredService<IEnumerable<IAsyncInitializer>>()
                         .GroupBy(i => i.Index)
                         .OrderBy(g => g.Key))
            {
                Task.WhenAll(group.Select(i => i.InitializeAsync())).WaitOnDispatcherFrame();
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "Failed to initialize application");

            NativeMessageBox.Show(
                "Initialization Error",
                $"An error occurred during application initialization:\n{ex.Message}\n\nPlease check the logs for more details.",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);
        }
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

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime:
            {
                ShowMainWindowOnNeeded();
                break;
            }
        }
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

        // If the --ui command line argument is present, show the main window.
        if (Environment.GetCommandLineArgs().Contains("--ui") || previousVersion < currentVersion)
        {
            ShowWindow<MainView>();
        }

        persistentState.PreviousLaunchVersion = currentVersion.ToString();
    }



    private void ShowWindow<TContent>() where TContent : Control
    {
        if (_isShowWindowBusy) return;
        try
        {
            _isShowWindowBusy = true;

            var windowType = typeof(TContent);
            _transientWindows.TryGetValue(windowType, out var window);

            if (window is { IsLoaded: true })
            {
                if (window.WindowState is WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                var topMost = window.Topmost;
                window.Topmost = true;
                window.Topmost = topMost;

                window.Activate();
            }
            else
            {
                if (window is not null)
                {
                    window.Content = null;
                    window.Close();
                }

                var content = serviceProvider.GetRequiredService<TContent>();
                content.To<ISetLogicalParent>().SetParent(null);
                window = new TransientWindow
                {
                    [SaveWindowPlacementAssist.KeyProperty] = typeof(TContent).FullName,
                    Content = content
                };
                _transientWindows[windowType] = window;

                window.Show();
            }
        }
        finally
        {
            _isShowWindowBusy = false;
        }
    }

    public void ShowMainWindow() => ShowWindow<MainView>();

    public void ShowDebugWindow() => ShowWindow<VisualTreeDebugger>();

    void IRecipient<ApplicationMessage>.Receive(ApplicationMessage message)
    {
        if (message is ShowWindowMessage { Name: ShowWindowMessage.MainWindow } showWindowMessage)
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                ShowWindow<MainView>();
                if (showWindowMessage.Route is not null) WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(showWindowMessage.Route));
            });
        }
    }
}
