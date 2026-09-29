using Everywhere.Automation;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

public sealed partial class AutomationHostSession
{
    private readonly Lock _textSelectionGate = new();
    private readonly SemaphoreSlim _textSelectionTransitionGate = new(1, 1);
    private TextSelectionMonitorRegistration? _textSelectionMonitor;
    private long _nextTextSelectionRevision;

    /// <inheritdoc />
    public async ValueTask<TextSelectionMonitoringControlResult> StartTextSelectionMonitoringAsync(
        StartTextSelectionMonitoringRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ContextId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MonitorId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MainProcessId);
        if (request.MainProcessId != Volatile.Read(ref _mainProcessId))
        {
            throw new InvalidOperationException("The text-selection monitor must identify the authenticated Main process.");
        }
        ValidateTextSelectionConfiguration(request.Configuration);

        await _textSelectionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = GetContext(request.ContextId);
            TextSelectionMonitorRegistration? current;
            TextSelectionMonitorRegistration? previous;
            lock (_textSelectionGate)
            {
                current = _textSelectionMonitor?.MonitorId == request.MonitorId ? _textSelectionMonitor : null;
                if (current is not null)
                {
                    if (current.ContextId != request.ContextId)
                    {
                        throw new InvalidOperationException("An existing text-selection monitor cannot be rebound to another Context.");
                    }
                }
                previous = _textSelectionMonitor;
            }
            if (current is not null)
            {
                await current.Monitor.UpdateConfigurationAsync(request.Configuration, cancellationToken).ConfigureAwait(false);
                return TextSelectionMonitoringControlResult.Success();
            }
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
                _resources.Consume(previous.MonitorId);
            }

            ITextSelectionMonitor monitor;
            try
            {
                monitor = _textSelectionMonitorFactory.Create(
                    context,
                    request.MainProcessId,
                    request.Configuration,
                    (observation, token) => PublishTextSelectionAsync(context, request.ContextId, request.MonitorId, observation, token));
            }
            catch (Exception exception) when (MapTextSelectionSetupFailure(exception) is { } failure)
            {
                return failure;
            }
            var registration = new TextSelectionMonitorRegistration(this, request.ContextId, request.MonitorId, monitor);
            lock (_textSelectionGate)
            {
                _textSelectionMonitor = registration;
            }

            await _resources.RegisterAsync(request.MonitorId, registration).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return TextSelectionMonitoringControlResult.Success();
        }
        finally
        {
            _textSelectionTransitionGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RpcAck> StopTextSelectionMonitoringAsync(
        StopTextSelectionMonitoringRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MonitorId);
        await _textSelectionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TextSelectionMonitorRegistration? registration;
            lock (_textSelectionGate)
            {
                registration = _textSelectionMonitor?.MonitorId == request.MonitorId ? _textSelectionMonitor : null;
            }

            if (registration is not null)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
                _resources.Consume(request.MonitorId);
            }
            return default;
        }
        finally
        {
            _textSelectionTransitionGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RpcAck> UpdateTextSelectionMonitoringAsync(
        UpdateTextSelectionMonitoringRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MonitorId);
        ValidateTextSelectionConfiguration(request.Configuration);

        await _textSelectionTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TextSelectionMonitorRegistration? registration;
            lock (_textSelectionGate)
            {
                registration = _textSelectionMonitor?.MonitorId == request.MonitorId ? _textSelectionMonitor : null;
            }

            if (registration is null)
            {
                throw new InvalidOperationException($"Text-selection monitor {request.MonitorId} is no longer active.");
            }

            await registration.Monitor.UpdateConfigurationAsync(request.Configuration, cancellationToken).ConfigureAwait(false);
            return default;
        }
        finally
        {
            _textSelectionTransitionGate.Release();
        }
    }

    private async ValueTask PublishTextSelectionAsync(
        AutomationContextResource context,
        long contextId,
        long monitorId,
        TextSelectionObservation observation,
        CancellationToken cancellationToken)
    {
        await using (observation.ConfigureAwait(false))
        {
            lock (_textSelectionGate)
            {
                if (_textSelectionMonitor?.MonitorId != monitorId) return;
            }

            var connection = _connection ?? throw new InvalidOperationException("The Automation Host is not bound to an RPC connection.");
            var notifications = _notifications ?? throw new InvalidOperationException("The Automation Host notification client is unavailable.");
            var anchorId = 0L;
            AcquireAutomationAnchorResponse? sourceResponse = null;
            var isAnchorRegistered = false;
            if (observation.Source is { } source)
            {
                anchorId = connection.AllocateResourceId();
                if (anchorId >= 0) throw new InvalidOperationException("Automation Host must allocate pushed resources from the negative ID range.");
                sourceResponse = await context.ExecuteAsync(
                    resource => resource.AddTextSelectionAnchor(anchorId, source),
                    cancellationToken).ConfigureAwait(false);
                observation.TakeSource();
                isAnchorRegistered = await _resources.RegisterAsync(
                    anchorId,
                    new AutomationAnchorRelease(context, anchorId)).ConfigureAwait(false);

                if (!isAnchorRegistered)
                {
                    return;
                }
            }

            try
            {
                var revision = Interlocked.Increment(ref _nextTextSelectionRevision);
                await notifications.TextSelectionObservedAsync(
                    new TextSelectionObservedNotification
                    {
                        ContextId = contextId,
                        MonitorId = monitorId,
                        Revision = revision,
                        Text = observation.Text,
                        AnchorId = anchorId,
                        Source = sourceResponse,
                        IsTextIncomplete = observation.IsTextIncomplete,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (isAnchorRegistered)
                {
                    await _resources.ReleaseResourceAsync(
                        new RpcResourceReleaseRequest
                        {
                            ResourceId = anchorId
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    private async ValueTask ReleaseTextSelectionMonitorAsync(TextSelectionMonitorRegistration registration)
    {
        await registration.Monitor.DisposeAsync().ConfigureAwait(false);
        lock (_textSelectionGate)
        {
            if (ReferenceEquals(_textSelectionMonitor, registration)) _textSelectionMonitor = null;
        }
    }

    private sealed class TextSelectionMonitorRegistration(
        AutomationHostSession owner,
        long contextId,
        long monitorId,
        ITextSelectionMonitor monitor
    ) : IAsyncDisposable
    {
        public long ContextId { get; } = contextId;
        public long MonitorId { get; } = monitorId;
        public ITextSelectionMonitor Monitor { get; } = monitor;

        private readonly Lock _disposeGate = new();
        private Task? _disposeTask;

        public ValueTask DisposeAsync()
        {
            Task disposeTask;
            lock (_disposeGate)
            {
                disposeTask = _disposeTask ??= owner.ReleaseTextSelectionMonitorAsync(this).AsTask();
            }
            return new ValueTask(disposeTask);
        }
    }

    private sealed partial class AutomationContextResource
    {
        ValueTask<TResult> ITextSelectionMonitorContext.ExecuteAsync<TResult>(
            Func<VisualContext, IVisualElementBackend, CancellationToken, TResult> operation,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                (resource, token) => ValueTask.FromResult(operation(resource.Context, resource._backend, token)),
                cancellationToken);

        async ValueTask ITextSelectionMonitorContext.ReleaseAsync(TextSelectionSource source)
        {
            await ExecuteAsync(
                _ =>
                {
                    source.ReleaseWithinContext();
                    return default(RpcAck);
                },
                CancellationToken.None).ConfigureAwait(false);
        }

        public AcquireAutomationAnchorResponse AddTextSelectionAnchor(long anchorId, TextSelectionSource source)
        {
            if (anchorId == 0) throw new ArgumentOutOfRangeException(nameof(anchorId));
            if (_anchors.ContainsKey(anchorId))
            {
                throw new InvalidOperationException($"Automation anchor {anchorId} is already registered in this Context.");
            }

            var response = source.Result.ToResponse();
            var retention = source.TakeRetention();
            try
            {
                _anchors.Add(anchorId, new AutomationAnchor(retention, source.Result));
                return response;
            }
            catch
            {
                retention.Dispose();
                throw;
            }
        }
    }

    private static TextSelectionMonitoringControlResult? MapTextSelectionSetupFailure(Exception exception)
    {
        VisualElementQueryFailureKind? kind = exception switch
        {
            UnauthorizedAccessException => VisualElementQueryFailureKind.PermissionDenied,
            NotSupportedException => VisualElementQueryFailureKind.Unsupported,
            System.ComponentModel.Win32Exception => VisualElementQueryFailureKind.ProviderFailure,
            InvalidOperationException => VisualElementQueryFailureKind.ProviderFailure,
            _ => null,
        };
        if (kind is null) return null;
        var result = new TextSelectionMonitoringControlResult
        {
            IsSucceeded = false,
            FailureKind = kind,
            Message = new DynamicLocaleKey(
                kind switch
                {
                    VisualElementQueryFailureKind.PermissionDenied => LocaleKey.ChatWindowSettings_TextSelectionMonitoring_PermissionDenied,
                    VisualElementQueryFailureKind.Unsupported => LocaleKey.ChatWindowSettings_TextSelectionMonitoring_Unsupported,
                    _ => LocaleKey.ChatWindowSettings_TextSelectionMonitoring_SetupFailed,
                }),
        };
        Serilog.Log.ForContext<AutomationHostSession>().Warning(
            exception,
            "Failed to start native text-selection monitoring with failure kind {FailureKind}.",
            kind);
        return result;
    }

    private static void ValidateTextSelectionConfiguration(TextSelectionMonitoringConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateApplications(configuration.ExcludedApplications);
        return;

        static void ValidateApplications(string[] applications)
        {
            // TODO: sync these limits to the configuration UI
            if (applications.Length > 256)
            {
                throw new InvalidDataException("A text-selection application filter contains too many entries.");
            }

            var totalLength = 0;
            foreach (var application in applications)
            {
                if (string.IsNullOrWhiteSpace(application) || application.Length > 32_768)
                {
                    throw new InvalidDataException("A text-selection application filter contains an invalid identity.");
                }
                totalLength = checked(totalLength + application.Length);
            }
            if (totalLength > 64 * 1024)
            {
                throw new InvalidDataException("A text-selection application filter exceeds its size limit.");
            }
        }
    }
}