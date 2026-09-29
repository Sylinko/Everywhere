namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Coordinates review of every file in one patch before any commit is started.
/// </summary>
public sealed class PatchReviewSession : IDisposable
{
    /// <summary>
    /// Gets the file review items in patch order.
    /// </summary>
    public IReadOnlyList<PatchReviewItem> Items { get; }

    private PatchReviewSession(IReadOnlyList<PatchReviewItem> items)
    {
        Items = items;
    }

    /// <summary>
    /// Creates review items and their existing text-difference models from a complete plan.
    /// </summary>
    public static PatchReviewSession Create(PatchPlan plan)
    {
        return new PatchReviewSession([.. plan.Files.Select(static file => new PatchReviewItem(file))]);
    }

    /// <summary>
    /// Requests consent for every actionable file and publishes only completed lightweight summaries.
    /// </summary>
    /// <param name="fileDecisionAsync">Requests the file-level consent decision.</param>
    /// <param name="displaySink">Receives completed review summaries.</param>
    /// <param name="cancellationToken">Cancels the pending review.</param>
    /// <returns>One decision for every planned source path.</returns>
    public async ValueTask<IReadOnlyList<PatchFileDecision>> ReviewAsync(
        Func<PatchReviewItem, CancellationToken, Task<RequestConsentResult>> fileDecisionAsync,
        IChatPluginDisplaySink displaySink,
        CancellationToken cancellationToken)
    {
        var decisions = new List<PatchFileDecision>(Items.Count);
        foreach (var item in Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await item.RequestDecisionAsync(fileDecisionAsync, cancellationToken);
            decisions.Add(decision);
            if (decision is PatchNoChangesFileDecision) continue;

            item.DisplayBlock.CompleteReview();
            displaySink.AppendBlock(item.DisplayBlock);
        }

        return decisions;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var item in Items) item.Dispose();
    }
}

/// <summary>
/// Represents one planned file and the DisplayBlock used by its consent request.
/// </summary>
public sealed class PatchReviewItem : IDisposable
{
    /// <summary>
    /// Gets the planned file operation.
    /// </summary>
    public PatchPlanFile File { get; }

    /// <summary>
    /// Gets the existing diff model used by the review controls.
    /// </summary>
    public TextDifference Difference { get; }

    /// <summary>
    /// Gets the block shown inside consent and later published as a completed summary.
    /// </summary>
    public ChatPluginFileDifferenceDisplayBlock DisplayBlock { get; }

    /// <summary>
    /// Gets the operation-specific presentation used by the difference summary.
    /// </summary>
    public TextDifferenceReviewKind ReviewKind => File switch
    {
        PatchAddPlanFile => TextDifferenceReviewKind.Create,
        PatchUpdatePlanFile => TextDifferenceReviewKind.Update,
        PatchDeletePlanFile => TextDifferenceReviewKind.Delete,
        PatchMovePlanFile { HasContentChange: true } => TextDifferenceReviewKind.MoveAndUpdate,
        PatchMovePlanFile => TextDifferenceReviewKind.Move,
        _ => throw new PatchReviewException($"The patch plan type '{File.GetType().Name}' is not supported.")
    };

    /// <summary>
    /// Gets whether the item has a content difference that can be reviewed change-by-change.
    /// </summary>
    public bool HasVisibleDifference => Difference.TotalChangesCount > 0;

    public PatchReviewItem(PatchPlanFile file)
    {
        File = file;
        Difference = file.CreateDifference();
        DisplayBlock = new ChatPluginFileDifferenceDisplayBlock(
            Difference,
            file.Original.Content,
            ReviewKind,
            file is PatchMovePlanFile ? file.SourcePath : null);
    }

    /// <summary>
    /// Requests consent and converts the resulting selection into a commit decision.
    /// </summary>
    public async Task<PatchFileDecision> RequestDecisionAsync(
        Func<PatchReviewItem, CancellationToken, Task<RequestConsentResult>> fileDecisionAsync,
        CancellationToken cancellationToken)
    {
        if (File is PatchUpdatePlanFile { HasContentChange: false })
        {
            return new PatchNoChangesFileDecision(File.SourcePath);
        }

        var fileDecision = await fileDecisionAsync(this, cancellationToken);
        if (!fileDecision.IsAccepted)
        {
            Difference.RejectAll();
            return new PatchRejectedFileDecision(File.SourcePath, fileDecision.Reason, CreateChangeDecisions());
        }

        var changes = CreateChangeDecisions();
        if (HasVisibleDifference && Difference.AcceptedChangesCount == 0 && File is not PatchMovePlanFile)
        {
            return new PatchRejectedFileDecision(File.SourcePath, null, changes);
        }

        return File switch
        {
            PatchDeletePlanFile => new PatchDeleteFileDecision(File.SourcePath, changes),
            _ => new PatchContentFileDecision(
                File.SourcePath,
                ResolveAcceptedContent(),
                changes)
        };
    }

    /// <summary>
    /// Uses the patch plan as the authoritative result unless the user explicitly rejects part of its derived diff.
    /// </summary>
    private string ResolveAcceptedContent() => Difference.RejectedChangesCount == 0 ?
        File.ProposedContent :
        Difference.Apply(File.Original.Content);

    private PatchChangeDecision[] CreateChangeDecisions()
    {
        var originalContent = File.Original.Content;
        return Difference
            .GetFilteredChanges(false)
            .AsValueEnumerable()
            .Select(change =>
            {
                var addedLineCount = change.Kind is TextChangeKind.Insert or TextChangeKind.Replace ?
                    TextDifference.CountLines(change.NewText) :
                    0;
                var removedLineCount = change.Kind is TextChangeKind.Delete or TextChangeKind.Replace ?
                    TextDifference.CountLines(change.GetOriginalSlice(originalContent)) :
                    0;
                return new PatchChangeDecision(
                    change.Id,
                    change.IsAccepted,
                    addedLineCount,
                    removedLineCount,
                    string.IsNullOrWhiteSpace(change.ReviewComment) ? null : change.ReviewComment.Trim());
            })
            .ToArray();
    }

    /// <inheritdoc/>
    public void Dispose() => Difference.Dispose();
}

/// <summary>
/// Reports a review flow that cannot safely produce a complete decision set.
/// </summary>
public sealed class PatchReviewException(string message) : InvalidOperationException(message);