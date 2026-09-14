using System.Text;
using Everywhere.Automation;

namespace Everywhere.ProcessIsolation.Automation;

public sealed partial class AutomationHostSession
{
    /// <inheritdoc />
    public ValueTask<AutomationVisualTreeResponse> InspectAnchorAsync(
        InspectAutomationAnchorRequest request,
        CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteAsync(
            (resource, token) => ValueTask.FromResult(resource.InspectAnchor(request, token)),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<AcquireAutomationAnchorResponse> GetTargetSnapshotAsync(
        GetAutomationTargetSnapshotRequest request,
        CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteAsync(
            resource => resource.GetTargetSnapshot(request),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<WriteAutomationVisualTreeFileResponse> WriteVisualTreeFileAsync(
        WriteAutomationVisualTreeFileRequest request,
        CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteAsync(
            (resource, token) => resource.WriteVisualTreeFileAsync(request, token),
            cancellationToken);

    private sealed partial class AutomationContextResource
    {
        public AutomationVisualTreeResponse InspectAnchor(
            InspectAutomationAnchorRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumNodes);
            var anchor = GetAnchor(request.AnchorId);
            var defaultLimits = VisualContextSnapshotLimits.Default;
            var limits = defaultLimits with
            {
                MaximumNodes = request.MaximumNodes,
                MaximumChildrenPerNode = Math.Min(defaultLimits.MaximumChildrenPerNode, request.MaximumNodes),
            };
            using var snapshot = VisualContextSnapshotter.CreateSnapshot(
                Context,
                [anchor.Result.Element],
                limits,
                cancellationToken: cancellationToken);
            EnsureTurn();
            var publication = Context.BeginPublication();
            var roots = snapshot.Roots.Select(node => CreateDiagnosticNode(node, publication)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            publication.Commit();
            return new AutomationVisualTreeResponse
            {
                Roots = roots,
                Status = [.. snapshot.Status],
            };
        }

        public AcquireAutomationAnchorResponse GetTargetSnapshot(GetAutomationTargetSnapshotRequest request)
        {
            using var retention = Context.CreateRetention();
            var element = RetainTarget(request.TargetId, retention);
            return element.Query(new VisualElementQueryRequest(request.RequestedFields, request.MaxTextCharacters)).ToResponse();
        }

        public async ValueTask<WriteAutomationVisualTreeFileResponse> WriteVisualTreeFileAsync(
            WriteAutomationVisualTreeFileRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TargetTokenBudget);
            if (request.TargetIds.Length == 0) throw new ArgumentException("At least one diagnostic target is required.", nameof(request));

            var content = new StringBuilder();
            foreach (var targetId in request.TargetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await new VisualQuery(Context).ExecuteAsync(
                    targetId,
                    new VisualQueryRequest
                    {
                        Directions = VisualContextTraverseDirections.All,
                        Limit = VisualQueryRequest.MaximumLimit,
                    },
                    new VisualContextPromptOptions { TargetTokenBudget = request.TargetTokenBudget },
                    cancellationToken).ConfigureAwait(false);
                if (content.Length > 0) content.AppendLine().AppendLine();
                content.Append(result.Content);
            }

            var filePath = Path.Combine(
                Path.GetTempPath(),
                $"Everywhere_visual_tree_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");
            await File.WriteAllTextAsync(filePath, content.ToString(), cancellationToken).ConfigureAwait(false);
            return new WriteAutomationVisualTreeFileResponse { FilePath = filePath };
        }

        private static AutomationVisualTreeNode CreateDiagnosticNode(
            VisualContextSnapshotNode node,
            VisualTargetPublicationBatch publication) =>
            new()
            {
                TargetId = publication.Add(new ElementTarget { Element = node.Element }),
                Observation = node.ToResponse(),
                Children = node.Children.Select(child => CreateDiagnosticNode(child, publication)).ToArray(),
                Status = [.. node.Status],
            };
    }
}