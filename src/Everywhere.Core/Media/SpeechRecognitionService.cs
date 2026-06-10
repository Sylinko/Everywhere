using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Media.Audio;
using Everywhere.Media.SpeechRecognition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObservableCollections;

namespace Everywhere.Media;

/// <summary>
/// Provides speech recognition services, managing available speech recognition engines and handling the lifecycle of speech recognition sessions.
/// This service is responsible for initializing speech recognition engines, starting and stopping speech recognition based on user input, and maintaining the state of active speech recognition runs.
/// It also handles exceptions that may occur during speech recognition and logs relevant information for diagnostics.
/// </summary>
public sealed partial class SpeechRecognitionService : ObservableObject, ISpeechRecognitionService, IAsyncInitializer, IDisposable
{
    public bool IsInitialized { get; private set; }

    public bool IsAvailable => IsInitialized && SelectedEngine is { IsSupported: true };

    [ObservableProperty]
    public partial SpeechRecognitionStatus Status { get; private set; } = SpeechRecognitionStatus.NotAvailable;

    public IReadOnlyBindableList<ISpeechRecognitionEngine> Engines { get; }

    public ISpeechRecognitionEngine? SelectedEngine
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            _speechRecognitionSettings.SelectedEngineId = value?.Id;
            OnPropertyChanged(nameof(IsAvailable));
            if (Volatile.Read(ref _activeRun) is null)
            {
                Status = GetIdleStatus();
            }
        }
    }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Media;

    private readonly SpeechRecognitionSettings _speechRecognitionSettings;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMicrophoneDeviceManager _microphoneDeviceManager;
    private readonly ILogger<SpeechRecognitionService> _logger;

    private readonly ObservableList<ISpeechRecognitionEngine> _enginesSource = [];
    private readonly IDisposable? _enginesSourceSubscription;

    private ActiveRun? _activeRun;

    public SpeechRecognitionService(
        Settings settings,
        IServiceProvider serviceProvider,
        IMicrophoneDeviceManager microphoneDeviceManager,
        ILogger<SpeechRecognitionService> logger)
    {
        _speechRecognitionSettings = settings.SpeechRecognition;
        _serviceProvider = serviceProvider;
        _microphoneDeviceManager = microphoneDeviceManager;
        _logger = logger;

        Engines = _enginesSource.ToNotifyCollectionChangedSlim().ToReadOnlyBindableList(out _enginesSourceSubscription);
    }

    public Task InitializeAsync() => Task.Run(async () =>
    {
        foreach (var engine in _serviceProvider.GetServices<ISpeechRecognitionEngine>())
        {
            try
            {
                await engine.InitializeAsync();

                // Only add the engine if it initializes successfully.
                _enginesSource.Add(engine);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize speech recognition engine {EngineId}. Skipping this engine.", engine.Id);
            }
        }

        SelectedEngine =
            _enginesSource.FirstOrDefault(engine => engine.Id == _speechRecognitionSettings.SelectedEngineId && engine.IsSupported) ??
            _enginesSource.FirstOrDefault(engine => engine.IsSupported);
        IsInitialized = true;
    });

    public SpeechRecognitionInputState? TryCreateInputState(SpeechRecognitionActivationKind activationKind)
    {
        if (!IsAvailable) return null;

        var state = new SpeechRecognitionInputState(activationKind);
        var run = new ActiveRun(state);
        if (Interlocked.CompareExchange(ref _activeRun, run, null) is not null) return null;

        Status = SpeechRecognitionStatus.Busy;
        return state;
    }

    public async Task StartSpeechRecognitionAsync(
        SpeechRecognitionInputState state,
        LocaleName? locale = null,
        CancellationToken cancellationToken = default)
    {
        var run = GetActiveRun(state);
        if (run is null) return;

        try
        {
            var engine = SelectedEngine;
            if (engine is not { IsSupported: true })
            {
                Status = SpeechRecognitionStatus.NotAvailable;
                return;
            }

            Status = SpeechRecognitionStatus.Busy;
            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                run.CancellationTokenSource.Token,
                cancellationToken);
            var linkedCancellationToken = linkedCancellationTokenSource.Token;

            await using var session = await engine
                .CreateSessionAsync(locale ?? LocaleManager.CurrentLocale, linkedCancellationToken)
                .ConfigureAwait(false);
            run.Session = session;

            if (run.StopRequested)
            {
                await session.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            }

            switch (session)
            {
                case ISystemHostedSpeechRecognitionSession systemHosted:
                    await foreach (var update in systemHosted.RecognizeAsync(linkedCancellationToken).ConfigureAwait(false))
                    {
                        if (!ReferenceEquals(Volatile.Read(ref _activeRun), run)) break;
                        if (!HandleSpeechRecognitionUpdate(run, update)) break;
                    }
                    break;
                case ICustomHostedSpeechRecognitionSession customHosted:
                    await RunCustomHostedSessionAsync(run, customHosted, linkedCancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var handledException = HandledSystemException.Handle(ex);
            state.LastException = handledException;
            _logger.LogError(handledException, "Failed to run speech recognition");
        }
        finally
        {
            await DisposeCaptureAsync(run).ConfigureAwait(false);
            CleanupActiveRun(run);
        }
    }

    public async Task StopSpeechRecognitionAsync(SpeechRecognitionInputState state, CancellationToken cancellationToken = default)
    {
        var run = GetActiveRun(state);
        if (run is null) return;
        if (!run.RequestStop()) return;

        try
        {
            var session = run.Session;
            if (session is not null)
            {
                if (run.Capture is { } capture)
                {
                    await capture.StopAsync(cancellationToken).ConfigureAwait(false);
                }

                await session.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await run.CancellationTokenSource.CancelAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var handledException = HandledSystemException.Handle(ex);
            state.LastException = handledException;
            _logger.LogError(handledException, "Failed to stop speech recognition");
        }
        finally
        {
            if (!run.CancellationTokenSource.IsCancellationRequested)
            {
                run.CancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(2));
            }
        }
    }

    public void Dispose()
    {
        _enginesSourceSubscription?.Dispose();
        Volatile.Read(ref _activeRun)?.CancellationTokenSource.Cancel();
    }

    private ActiveRun? GetActiveRun(SpeechRecognitionInputState state)
    {
        var run = Volatile.Read(ref _activeRun);
        return run is not null && ReferenceEquals(run.State, state) ? run : null;
    }

    private async Task RunCustomHostedSessionAsync(
        ActiveRun run,
        ICustomHostedSpeechRecognitionSession session,
        CancellationToken cancellationToken)
    {
        await using var capture = _microphoneDeviceManager.CreateCapture();
        run.Capture = capture;
        await capture.StartAsync(session.MicrophoneCaptureOptions, cancellationToken).ConfigureAwait(false);

        if (run.StopRequested)
        {
            await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await session.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await foreach (var update in session.RecognizeAsync(capture.Frames, cancellationToken).ConfigureAwait(false))
        {
            if (!ReferenceEquals(Volatile.Read(ref _activeRun), run)) break;
            if (!HandleSpeechRecognitionUpdate(run, update)) break;
        }
    }

    private async ValueTask DisposeCaptureAsync(ActiveRun run)
    {
        if (run.Capture is null) return;

        try
        {
            await run.Capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop microphone capture during cleanup.");
        }

        try
        {
            await run.Capture.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose microphone capture during cleanup.");
        }
    }

    private bool HandleSpeechRecognitionUpdate(ActiveRun run, SpeechRecognitionUpdate update)
    {
        var state = run.State;
        switch (update)
        {
            case SpeechRecognitionUpdate.Started:
                Status = SpeechRecognitionStatus.Recording;
                return true;
            case SpeechRecognitionUpdate.Hypothesis hypothesis:
                state.Composition = hypothesis.Text.Trim();
                return true;
            case SpeechRecognitionUpdate.Final final:
            {
                var text = final.Text.Trim();
                if (text.Length == 0) return true;

                state.Composition = null;
                state.RequestCommit(text);
                return true;
            }
            case SpeechRecognitionUpdate.Reset:
                state.Composition = null;
                state.RequestCompositionReset();
                return true;
            case SpeechRecognitionUpdate.Completed:
                return false;
            case SpeechRecognitionUpdate.Diagnostic diagnostic:
                _logger.Log(diagnostic.Level, "Speech recognition diagnostic: {Message}", diagnostic.Message);
                return true;
            default:
                return true;
        }
    }

    private void CleanupActiveRun(ActiveRun run)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _activeRun, null, run), run)) return;

        try
        {
            run.CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException) { }
        run.CancellationTokenSource.Dispose();

        var state = run.State;
        if (run.StopRequested && state.Composition?.Trim() is { Length: > 0 } composition)
        {
            state.RequestCommit(composition);
        }

        state.Composition = null;
        state.IsActive = false;
        Status = GetIdleStatus();
    }

    private SpeechRecognitionStatus GetIdleStatus() =>
        IsAvailable ? SpeechRecognitionStatus.Standby : SpeechRecognitionStatus.NotAvailable;

    private sealed class ActiveRun(SpeechRecognitionInputState state)
    {
        public SpeechRecognitionInputState State { get; } = state;

        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public ISpeechRecognitionSession? Session
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Volatile.Read(ref field);
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Volatile.Write(ref field, value);
        }

        public IMicrophoneCapture? Capture
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Volatile.Read(ref field);
            [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Volatile.Write(ref field, value);
        }

        public bool StopRequested
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Volatile.Read(ref _stopRequested) != 0;
        }

        private int _stopRequested;

        /// <summary>
        /// Requests to stop the active run. This will signal the recognition loop to stop, and will attempt to complete the recognition session if it has been created. This method is idempotent and thread-safe.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool RequestStop() => Interlocked.Exchange(ref _stopRequested, 1) == 0;
    }
}