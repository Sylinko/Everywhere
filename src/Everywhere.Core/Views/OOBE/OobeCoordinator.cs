using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Views;

/// <summary>
/// Minimal application-shell hooks used by <see cref="OobeCoordinator"/> to coordinate
/// onboarding windows with the main window and application shutdown without coupling
/// <see cref="App"/> to onboarding flows.
/// </summary>
public interface IOobeHost
{
    /// <summary>
    /// Gets whether the host application has begun shutting down.
    /// </summary>
    bool IsShuttingDown { get; }

    /// <summary>
    /// Hides the main window if it is currently visible.
    /// </summary>
    /// <returns><see langword="true"/> when a visible main window was hidden; otherwise <see langword="false"/>.</returns>
    bool TryHideMainWindow();

    /// <summary>
    /// Shows or focuses the main window.
    /// </summary>
    void ShowMainWindow();
}

/// <summary>
/// Describes a single self-contained onboarding window session hosted by <see cref="OobeCoordinator"/>.
/// Future feature introductions can supply any custom <see cref="Avalonia.Controls.Window"/> and
/// teardown callback without coupling to <see cref="WelcomeView"/> or <see cref="OobeFlow"/>.
/// </summary>
public sealed class OobeSession(
    Window window,
    Func<bool>? handlePostClose = null,
    Action? onDisposed = null
) : IDisposable
{
    /// <summary>
    /// Gets the window presenting this onboarding session.
    /// </summary>
    public Window Window { get; } = window;

    /// <summary>
    /// Gets an optional post-close callback invoked on the UI thread after the window closes and
    /// owned resources are disposed. Return <see langword="true"/> when the session handled
    /// post-close navigation itself so the coordinator does not restore the main window.
    /// </summary>
    public Func<bool>? HandlePostClose { get; } = handlePostClose;

    private bool _isDisposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        onDisposed?.Invoke();
    }
}

/// <summary>
/// Single entry point owning onboarding version resolution, single-instance window hosting,
/// resource disposal, and main-window coordination.
/// </summary>
public sealed class OobeCoordinator(
    Settings settings,
    IWindowHelper windowHelper,
    IServiceProvider serviceProvider
)
{
    /// <summary>
    /// Baseline assigned to migrated legacy installations that already had configured assistants
    /// before <see cref="Settings.CompletedOobeVersion"/> existed.
    /// </summary>
    public const int LegacyBaselineVersion = 1;

    /// <summary>
    /// Version written when the full welcome onboarding completes. Always matches the latest
    /// onboarding version so fresh installs are not immediately shown upgrade-only introductions.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Minimum version required to skip the initial welcome onboarding at startup.
    /// </summary>
    public const int WelcomeRequiredVersion = LegacyBaselineVersion;

    /// <summary>
    /// Gets whether an onboarding window is currently active.
    /// </summary>
    public bool IsActive => _activeWindow is not null;

    private IOobeHost? _host;
    private Window? _activeWindow;
    private bool _hasCheckedStartup;

    /// <summary>
    /// Attaches the application shell host so onboarding sessions can coordinate main-window
    /// visibility and respect application shutdown.
    /// </summary>
    public void AttachHost(IOobeHost host) => _host = host;

    /// <summary>
    /// Focuses the currently active onboarding window when one is open.
    /// </summary>
    /// <returns><see langword="true"/> when an active onboarding window was focused; otherwise <see langword="false"/>.</returns>
    public bool TryFocusActive()
    {
        if (_activeWindow is not { } existing) return false;

        FocusWindow(existing);
        return true;
    }

    /// <summary>
    /// Focuses an active onboarding window or, on the first startup check, opens the applicable
    /// onboarding session for <see cref="Settings.CompletedOobeVersion"/>.
    /// </summary>
    /// <returns><see langword="true"/> when an onboarding window is now active; otherwise <see langword="false"/>.</returns>
    public bool TryShowPendingStartup()
    {
        if (TryFocusActive()) return true;
        if (_hasCheckedStartup) return false;

        _hasCheckedStartup = true;
        if (ResolvePendingStartupSessionFactory() is not { } createSession)
        {
            return false;
        }

        ShowSession(createSession);
        return true;
    }

    /// <summary>
    /// Opens the full welcome onboarding experience, or focuses the already active onboarding window.
    /// </summary>
    public void ShowWelcome()
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_host?.IsShuttingDown == true) return;

            ShowSession(CreateWelcomeSession);
        });
    }

    /// <summary>
    /// Hosts an onboarding session produced lazily by <paramref name="createSession"/>. When an
    /// onboarding window is already active, it is focused without invoking <paramref name="createSession"/>.
    /// </summary>
    /// <returns><see langword="true"/> when a new session was opened; <see langword="false"/> when an existing window was focused.</returns>
    public bool ShowSession(Func<OobeSession> createSession)
    {
        if (TryFocusActive()) return false;

        _hasCheckedStartup = true;
        var session = createSession();
        var window = session.Window;
        _activeWindow = window;
        var wasMainWindowVisible = false;
        window.Closed += HandleClosed;
        try
        {
            windowHelper.InitializeWindow(window);
            wasMainWindowVisible = _host?.TryHideMainWindow() ?? false;
            window.Show();
            return true;
        }
        catch
        {
            window.Closed -= HandleClosed;
            _activeWindow = null;
            session.Dispose();
            window.Close();
            if (wasMainWindowVisible) _host?.ShowMainWindow();
            throw;
        }

        void HandleClosed(object? sender, EventArgs args)
        {
            window.Closed -= HandleClosed;
            _activeWindow = null;
            session.Dispose();

            Dispatcher.UIThread.Post(() =>
            {
                if (_host?.IsShuttingDown == true || _activeWindow is not null) return;
                if (session.HandlePostClose?.Invoke() == true) return;

                _host?.ShowMainWindow();
            });
        }
    }

    /// <summary>
    /// Creates an <see cref="OobeSession"/> wrapping a multi-step <see cref="OobeFlow"/> inside
    /// the standard borderless <see cref="WelcomeView"/> window.
    /// </summary>
    public OobeSession CreateFlowSession(OobeFlow flow)
    {
        var window = CreateWelcomeWindow(flow);

        void HandleCloseRequested(object? sender, EventArgs args) => window.Close();

        flow.CloseRequested += HandleCloseRequested;
        return new OobeSession(
            window,
            handlePostClose: () =>
            {
                if (!flow.ShouldStartUsing) return false;

                flow.StartUsing();
                return true;
            },
            onDisposed: () =>
            {
                flow.CloseRequested -= HandleCloseRequested;
                flow.Dispose();
            });
    }

    private Func<OobeSession>? ResolvePendingStartupSessionFactory()
    {
        if (settings.CompletedOobeVersion < WelcomeRequiredVersion)
        {
            return CreateWelcomeSession;
        }

        // Future upgrade-only feature introductions plug in here as simple version checks:
        // if (settings.CompletedOobeVersion < FeatureXVersion) return CreateFeatureXSession;
        return null;
    }

    private OobeSession CreateWelcomeSession() =>
        CreateFlowSession(serviceProvider.GetRequiredService<WelcomeFlow>());

    private static TransientWindow CreateWelcomeWindow(OobeFlow flow) =>
        new()
        {
            Width = 1180,
            Height = 810,
            MinWidth = 800,
            MinHeight = 600,
            CanMaximize = false,
            WindowDecorations = WindowDecorations.BorderOnly,
            TitleBarContent = null,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            BorderThickness = new Thickness(OperatingSystem.IsWindows() ? 1 : 0),
            [!TemplatedControl.BorderBrushProperty] = new DynamicResourceExtension("BorderColor"),
            Content = new WelcomeView(flow)
        };

    private static void FocusWindow(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        var isTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = isTopmost;
        window.Activate();
    }
}
