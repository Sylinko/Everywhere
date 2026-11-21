using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.Initialization;
using Everywhere.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Everywhere.Core.Tests.Common;

public class AppLifetimeTests
{
    private static readonly string[] ExpectedCallbackCalls = ["first"];
    private static readonly string[] ExpectedRestartArguments = ["--ui", "--first"];

    [AvaloniaTest]
    public async Task Initializers_AfterPreviousGroupCompletes_ResolveNextGroupAndPreserveSingletonOwnership()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationToken = CancellationToken.None;
        var probe = new StartupProbe
        {
            InitializeFirst = async token =>
            {
                applicationToken = token;
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var services = CreateServices()
            .AddSingleton(probe)
            .AddSingleton<LaterInitializer>()
            .AddAsyncInitializer<LaterInitializer>()
            .AddAsyncInitializer<FirstInitializer>()
            .BuildServiceProvider();
        using var app = new App(services);
        await using var ownedServices = services;

        var startup = Task.Run(() => InitializeServicesAsync(app));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(probe.LaterConstructionCount, Is.Zero);
        release.SetResult();
        await startup.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(probe.LaterConstructionCount, Is.EqualTo(1));
        Assert.That(probe.InitializedLater, Is.SameAs(services.GetRequiredService<LaterInitializer>()));
        Assert.That(applicationToken.IsCancellationRequested, Is.False);
        using var registration = applicationToken.Register(() => probe.HasObservedCancellation = true);
        await app.WaitForShutdownAsync();
        Assert.That(probe.HasObservedCancellation, Is.True);
        Assert.That(probe.DisposalCount, Is.Zero, "App borrows services; the container owns them.");
    }

    [AvaloniaTest]
    public async Task Initializers_WhenOneStepFails_CancelsAndDrainsSiblingWithoutConstructingNextGroup()
    {
        var siblingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasSiblingFinished = false;
        var probe = new StartupProbe
        {
            InitializeFirst = async token =>
            {
                await siblingEntered.Task.WaitAsync(token);
                throw new InvalidOperationException("Database migration failed.");
            },
            InitializeSibling = async token =>
            {
                siblingEntered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    hasSiblingFinished = true;
                }
            }
        };
        var services = CreateServices()
            .AddSingleton(probe)
            .AddAsyncInitializer<FirstInitializer>()
            .AddAsyncInitializer<SiblingInitializer>()
            .AddAsyncInitializer<LaterInitializer>()
            .BuildServiceProvider();
        using var app = new App(services);
        await using var ownedServices = services;

        var startup = Task.Run(() => InitializeServicesAsync(app));
        Exception? failure = null;
        try
        {
            await startup.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.That(failure, Is.TypeOf<InvalidOperationException>());

        Assert.That(hasSiblingFinished, Is.True);
        Assert.That(probe.LaterConstructionCount, Is.Zero);
    }

    [AvaloniaTest]
    public async Task Cancellation_WithQueuedUiInitialization_CancelsWithoutRunningTheUiCallback()
    {
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasRunUiCallback = false;
        var probe = new StartupProbe
        {
            InitializeFirst = token =>
            {
                // Inactive work is never pumped. This represents UI work still queued when the
                // application stops, rather than relying on timing or shutting down the test dispatcher.
                var operation = Dispatcher.UIThread.InvokeAsync(
                    () => hasRunUiCallback = true,
                    DispatcherPriority.Inactive,
                    token);
                queued.SetResult();
                return operation.GetTask();
            }
        };
        var services = CreateServices()
            .AddSingleton(probe)
            .AddAsyncInitializer<FirstInitializer>()
            .AddAsyncInitializer<LaterInitializer>()
            .BuildServiceProvider();
        using var app = new App(services);
        app.ApplicationLifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        await using (services)
        {
            app.OnFrameworkInitializationCompleted();
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            app.Shutdown();
            await app.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(hasRunUiCallback, Is.False);
            Assert.That(probe.LaterConstructionCount, Is.Zero);
            Assert.That(probe.DisposalCount, Is.Zero);
        }
        Assert.That(probe.DisposalCount, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ShutdownRequested_DuringPendingStartup_CancelsAndDrainsBeforeCallingLifetimeShutdown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupDrained = false;
        var probe = new StartupProbe
        {
            InitializeFirst = async token =>
            {
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => startupDrained = true);
                }
            }
        };
        var lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        var services = CreateServices()
            .AddSingleton(probe)
            .AddAsyncInitializer<FirstInitializer>()
            .BuildServiceProvider();
        using var app = new App(services);
        app.ApplicationLifetime = lifetime;
        await using (services)
        {
            app.OnFrameworkInitializationCompleted();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var args = new ShutdownRequestedEventArgs();
            lifetime.ShutdownRequested += Raise.Event<EventHandler<ShutdownRequestedEventArgs>>(lifetime, args);
            Assert.That(args.Cancel, Is.True);

            await app.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();
            Assert.That(startupDrained, Is.True);
            lifetime.Received(1).Shutdown();
        }
    }

    [AvaloniaTest]
    public async Task Shutdown_WithOnExited_DrainsOnceIsolatesExceptionsAndIgnoresSubsequentRequests()
    {
        var previousLogger = Log.Logger;
        using var sink = new CollectingSink();
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var services = CreateServices()
            .AddApplicationLogging()
            .BuildServiceProvider();
        var app = new App(services);
        var calls = new List<string>();

        try
        {
            await using (services)
            {
                services.GetRequiredService<ILoggerFactory>().CreateLogger("test");
                app.Shutdown(onExited: () =>
                {
                    calls.Add("first");
                    throw new InvalidOperationException("Boom");
                });
                app.Shutdown(onExited: () => calls.Add("second"));
                app.Shutdown();
                Dispatcher.UIThread.RunJobs();

                await CompleteDesktopShutdownAsync(null, app);
            }

            Assert.That(sink.IsDisposed, Is.False);
            Assert.That<Action?>(GetEntranceOnExited(), Is.Not.Null);

            CompleteEntranceProcessExit();

            Assert.That(calls, Is.EqualTo(ExpectedCallbackCalls));
            Assert.That(sink.IsDisposed, Is.True);
            Assert.That(
                sink.Events,
                Has.One.Matches<LogEvent>(e =>
                    e is not null &&
                    e.Level == LogEventLevel.Error &&
                    e.Exception is InvalidOperationException { Message: "Boom" }));
            Assert.That<Action?>(GetEntranceOnExited(), Is.Null);

            CompleteEntranceProcessExit();
            Assert.That(calls, Is.EqualTo(ExpectedCallbackCalls));
        }
        finally
        {
            Entrance.RegisterExitedCallback(null);
            Log.Logger = previousLogger;
        }
    }

    [AvaloniaTest]
    public async Task Restart_WithArguments_SchedulesRelaunchOnFirstRequestAndIgnoresDuplicateRestart()
    {
        var lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        await using var services = CreateServices().BuildServiceProvider();
        using var app = new App(services);
        app.ApplicationLifetime = lifetime;

        try
        {
            app.Restart("--ui", "--first");
            app.Restart("--second");
            Dispatcher.UIThread.RunJobs();

            var scheduledCallback = GetEntranceOnExited();
            Assert.That<Action?>(scheduledCallback, Is.Not.Null);
            if (scheduledCallback is not null)
            {
                Assert.That(ExtractCapturedArguments(scheduledCallback), Is.EqualTo(ExpectedRestartArguments));
            }

            lifetime.Received(1).Shutdown();
        }
        finally
        {
            Entrance.RegisterExitedCallback(null);
        }
    }

    private static Action? GetEntranceOnExited()
    {
        var field = typeof(Entrance).GetField("_onExited", BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingFieldException(typeof(Entrance).FullName, "_onExited");
        return (Action?)field.GetValue(null);
    }

    private static void CompleteEntranceProcessExit()
    {
        var method = typeof(Entrance).GetMethod("CompleteProcessExit", BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new MissingMethodException(typeof(Entrance).FullName, "CompleteProcessExit");
        method.Invoke(null, null);
    }

    private static IReadOnlyList<string>? ExtractCapturedArguments(Action callback)
    {
        var target = callback.Target;
        if (target is null)
        {
            return null;
        }

        foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(target) is IReadOnlyList<string> arguments)
            {
                return arguments;
            }
        }

        return null;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "CompleteDesktopShutdownAsync")]
    private static extern Task CompleteDesktopShutdownAsync(App? _, App? app);

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<INativeHelper>());
        services.AddSingleton(Substitute.For<IWindowHelper>());
        services.AddSingleton(Substitute.For<IKeyValueStorage>());
        return services;
    }

    // Exercise the production ordering routine without publishing a tray or replacing the
    // shared headless Application. Desktop startup and exit use the public App entry points.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "InitializeServicesAsync")]
    private static extern Task InitializeServicesAsync(App app);

    private sealed class StartupProbe
    {
        public Func<CancellationToken, Task> InitializeFirst { get; init; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> InitializeSibling { get; init; } = _ => Task.CompletedTask;
        public int LaterConstructionCount { get; set; }
        public int DisposalCount { get; set; }
        public bool HasObservedCancellation { get; set; }
        public LaterInitializer? InitializedLater { get; set; }
    }

    private sealed class FirstInitializer(StartupProbe probe) : IAsyncInitializer, IDisposable
    {
        public static AsyncInitializerIndex Index => AsyncInitializerIndex.Database;

        public Task InitializeAsync(CancellationToken cancellationToken) => probe.InitializeFirst(cancellationToken);

        public void Dispose() => probe.DisposalCount++;
    }

    private sealed class SiblingInitializer(StartupProbe probe) : IAsyncInitializer
    {
        public static AsyncInitializerIndex Index => AsyncInitializerIndex.Database;

        public Task InitializeAsync(CancellationToken cancellationToken) => probe.InitializeSibling(cancellationToken);
    }

    private sealed class LaterInitializer : IAsyncInitializer
    {
        public static AsyncInitializerIndex Index => AsyncInitializerIndex.Settings;

        private readonly StartupProbe _probe;

        public LaterInitializer(StartupProbe probe)
        {
            _probe = probe;
            probe.LaterConstructionCount++;
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            _probe.InitializedLater = this;
            return Task.CompletedTask;
        }
    }

    private sealed class CollectingSink : ILogEventSink, IDisposable
    {
        public List<LogEvent> Events { get; } = [];
        public bool IsDisposed { get; private set; }

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);

        public void Dispose() => IsDisposed = true;
    }
}
