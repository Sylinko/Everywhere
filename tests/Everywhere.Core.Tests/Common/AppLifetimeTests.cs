using System.Runtime.CompilerServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Initialization;
using Everywhere.Modding.Hosting.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Everywhere.Core.Tests.Common;

public class AppLifetimeTests
{
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
        var services = new HostServiceContainer(CreateServices()
            .AddSingleton(probe)
            .AddSingleton<LaterInitializer>()
            .AddAsyncInitializer<LaterInitializer>()
            .AddAsyncInitializer<FirstInitializer>());
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
        var services = new HostServiceContainer(CreateServices()
            .AddSingleton(probe)
            .AddAsyncInitializer<FirstInitializer>()
            .AddAsyncInitializer<SiblingInitializer>()
            .AddAsyncInitializer<LaterInitializer>());
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
        var services = new HostServiceContainer(CreateServices()
            .AddSingleton(probe)
            .AddAsyncInitializer<FirstInitializer>()
            .AddAsyncInitializer<LaterInitializer>());
        using var app = new App(services)
        {
            ApplicationLifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>()
        };
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
}
