using Everywhere.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>
/// Owns a diagnostics-only Host Context whose completed turns and published targets are not retained.
/// </summary>
public sealed class DebuggerVisualContext : HostedVisualContext<RemoteDebuggerVisualContext>
{
    private const string ResetNotice = "The Automation Host was reset. The current diagnostic tree is no longer valid.";

    private readonly Lock _diagnosticStateGate = new();
    private bool _isDiagnosticTreeCurrent;

    internal DebuggerVisualContext(IHostConnectionSource connectionSource) : base(connectionSource)
    {
    }

    /// <summary>Releases the previous diagnostic turn and prepares a new bounded inspection.</summary>
    public async ValueTask BeginInspectionAsync(CancellationToken cancellationToken = default)
    {
        lock (_diagnosticStateGate)
        {
            _isDiagnosticTreeCurrent = false;
        }

        await AdvanceTurnAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds and publishes one bounded diagnostic tree around a current pre-publication anchor.</summary>
    public async ValueTask<AutomationVisualTreeResponse> InspectAnchorAsync(
        RemoteVisualAnchor anchor,
        int maximumNodes = InspectAutomationAnchorRequest.DefaultMaximumNodes,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(anchor.Context, state.Context)) throw CreateContextResetException();
        try
        {
            var response = await state.Context.InspectAnchorAsync(anchor, maximumNodes, cancellationToken).ConfigureAwait(false);
            OnTargetsPublished(state.Context);
            return response;
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    /// <summary>Returns a fresh field-selected observation of one current diagnostic target.</summary>
    public ValueTask<VisualElementSnapshot> GetTargetSnapshotAsync(
        int targetId,
        VisualElementFields requestedFields,
        int maxTextCharacters = 0,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            (context, token) => context.GetTargetSnapshotAsync(
                targetId,
                requestedFields,
                maxTextCharacters,
                token),
            requiresCurrentTargets: true,
            cancellationToken);

    /// <summary>Builds selected diagnostic targets and returns the Host-written local file path.</summary>
    public ValueTask<string> WriteVisualTreeFileAsync(
        IReadOnlyList<int> targetIds,
        int targetTokenBudget,
        CancellationToken cancellationToken = default) =>
        ExecutePublishingAsync(
            (context, token) => context.WriteVisualTreeFileAsync(targetIds, targetTokenBudget, token),
            requiresCurrentTargets: true,
            cancellationToken);

    private protected override string ContextResetNotice => ResetNotice;

    private protected override async ValueTask<RemoteDebuggerVisualContext> CreateRemoteContextAsync(
        RpcConnection connection,
        CancellationToken cancellationToken) =>
        await new AutomationHostClient(connection).CreateDebuggerContextAsync(cancellationToken).ConfigureAwait(false);

    private protected override void OnRemoteContextReplacing(RemoteDebuggerVisualContext? previous)
    {
        lock (_diagnosticStateGate)
        {
            _isDiagnosticTreeCurrent = false;
        }
    }

    private protected override void OnTargetsPublished(RemoteDebuggerVisualContext context)
    {
        if (!IsCurrentContext(context)) return;
        lock (_diagnosticStateGate)
        {
            _isDiagnosticTreeCurrent = true;
        }
    }

    private protected override void OnVisualContextInvalidated(RemoteDebuggerVisualContext context)
    {
        if (!IsCurrentContext(context)) return;
        lock (_diagnosticStateGate)
        {
            _isDiagnosticTreeCurrent = false;
        }
    }

    private protected override void ValidateTargetState(RemoteDebuggerVisualContext context)
    {
        lock (_diagnosticStateGate)
        {
            if (_isDiagnosticTreeCurrent) return;
        }

        throw CreateContextResetException();
    }
}