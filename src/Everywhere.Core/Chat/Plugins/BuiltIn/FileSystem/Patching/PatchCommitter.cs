using Everywhere.Common;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Describes one reviewed text change, its line impact, and its optional user comment.
/// </summary>
public sealed record PatchChangeDecision(
    string Id,
    bool Accepted,
    int AddedLineCount,
    int RemovedLineCount,
    string? Comment
);

/// <summary>
/// Carries one explicit review outcome for a planned file.
/// </summary>
public abstract record PatchFileDecision(string SourcePath, IReadOnlyList<PatchChangeDecision> Changes);

/// <summary>
/// Accepts a content-bearing add, update, or move operation.
/// </summary>
public sealed record PatchContentFileDecision(
    string SourcePath,
    string Content,
    IReadOnlyList<PatchChangeDecision> Changes
) : PatchFileDecision(SourcePath, Changes);

/// <summary>
/// Accepts a delete operation.
/// </summary>
public sealed record PatchDeleteFileDecision(
    string SourcePath,
    IReadOnlyList<PatchChangeDecision> Changes
) : PatchFileDecision(SourcePath, Changes);

/// <summary>
/// Records an operation rejected by the user, including an optional reason.
/// </summary>
public sealed record PatchRejectedFileDecision(
    string SourcePath,
    string? Reason,
    IReadOnlyList<PatchChangeDecision> Changes
) : PatchFileDecision(SourcePath, Changes);

/// <summary>
/// Records a planned update whose proposed content already matches the source.
/// </summary>
public sealed record PatchNoChangesFileDecision(string SourcePath) : PatchFileDecision(SourcePath, []);

/// <summary>
/// Describes the outcome for one planned file operation.
/// </summary>
public enum PatchCommitStatus
{
    Committed,
    NoChanges,
    RejectedByUser,
    Conflict,
    Failed,
    NotAttempted,
}

/// <summary>
/// Reports the outcome for one planned path.
/// </summary>
public sealed record PatchCommitFileResult(
    string Path,
    PatchCommitStatus Status,
    PatchFileDecision Decision,
    string? Error = null
);

/// <summary>
/// Reports the result of applying reviewed patch operations.
/// </summary>
public sealed record PatchCommitResult(
    bool Succeeded,
    IReadOnlyList<PatchCommitFileResult> Files,
    string? Error = null
);

/// <summary>
/// Applies reviewed patch results after rechecking their raw source snapshots.
/// </summary>
/// <remarks>
/// Planning and conflict verification happen before the first mutation. The commit itself is
/// deliberately sequential and does not create disk backups or attempt rollback. If an operation
/// fails after an earlier operation committed, the result reports the committed, failed, and
/// not-attempted operations so the caller can explain the partial outcome accurately.
/// </remarks>
public static class PatchCommitter
{
    /// <summary>
    /// Applies accepted decisions after rechecking every source snapshot.
    /// </summary>
    /// <param name="plan">The mutation-free plan to commit.</param>
    /// <param name="decisions">One decision for every planned source path.</param>
    /// <param name="limits">Safety limits used for final conflict reads.</param>
    /// <param name="cancellationToken">Cancels before or between file operations.</param>
    /// <returns>A detailed commit, skip, conflict, or partial-failure report.</returns>
    public static async ValueTask<PatchCommitResult> CommitAsync(
        PatchPlan plan,
        IReadOnlyList<PatchFileDecision> decisions,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        var decisionMap = new Dictionary<string, PatchFileDecision>(PathUtilities.SystemPathComparer);
        foreach (var decision in decisions)
        {
            if (!decisionMap.TryAdd(decision.SourcePath, decision))
            {
                throw new PatchCommitException($"The patch contains more than one review decision for '{decision.SourcePath}'.");
            }
        }

        if (decisionMap.Count != plan.Files.Count)
        {
            throw new PatchCommitException("The patch review returned a decision for an unknown file.");
        }

        var commitItems = new List<CommitItem>();
        var resultItems = new List<PatchCommitFileResult>(plan.Files.Count);
        foreach (var file in plan.Files)
        {
            if (!decisionMap.TryGetValue(file.SourcePath, out var decision))
            {
                throw new PatchCommitException($"The patch review did not return a decision for '{file.SourcePath}'.");
            }

            if (decision is PatchRejectedFileDecision)
            {
                resultItems.Add(new PatchCommitFileResult(file.ReviewPath, PatchCommitStatus.RejectedByUser, decision));
                continue;
            }

            if (decision is PatchNoChangesFileDecision)
            {
                resultItems.Add(new PatchCommitFileResult(file.ReviewPath, PatchCommitStatus.NoChanges, decision));
                continue;
            }

            switch (file)
            {
                case PatchAddPlanFile add when decision is PatchContentFileDecision contentDecision:
                    commitItems.Add(
                        new AddCommitItem(
                            add,
                            contentDecision,
                            EncodeAcceptedContent(add, contentDecision.Content, limits)));
                    break;
                case PatchUpdatePlanFile update when decision is PatchContentFileDecision contentDecision:
                    if (string.Equals(contentDecision.Content, update.Original.Content, StringComparison.Ordinal))
                    {
                        resultItems.Add(new PatchCommitFileResult(file.ReviewPath, PatchCommitStatus.NoChanges, decision));
                        break;
                    }

                    commitItems.Add(
                        new UpdateCommitItem(
                            update,
                            contentDecision,
                            EncodeAcceptedContent(update, contentDecision.Content, limits)));
                    break;
                case PatchDeletePlanFile delete when decision is PatchDeleteFileDecision deleteDecision:
                    commitItems.Add(new DeleteCommitItem(delete, deleteDecision));
                    break;
                case PatchMovePlanFile move when decision is PatchContentFileDecision contentDecision:
                    commitItems.Add(
                        new MoveCommitItem(
                            move,
                            contentDecision,
                            EncodeAcceptedContent(move, contentDecision.Content, limits)));
                    break;
                case PatchAddPlanFile or PatchUpdatePlanFile or PatchMovePlanFile:
                    throw new PatchCommitException(
                        $"The accepted review for '{file.ReviewPath}' did not provide file content.");
                case PatchDeletePlanFile:
                    throw new PatchCommitException(
                        $"The accepted review for '{file.ReviewPath}' did not provide a delete decision.");
                default:
                    throw new PatchCommitException($"The patch plan type '{file.GetType().Name}' is not supported.");
            }
        }

        if (commitItems.Count == 0)
        {
            return new PatchCommitResult(true, OrderResults(plan, resultItems));
        }

        var conflict = await VerifyCurrentStateAsync(commitItems, limits, cancellationToken);
        if (conflict is not null)
        {
            resultItems.AddRange(
                commitItems.Select(item => new PatchCommitFileResult(
                    item.Plan.ReviewPath,
                    PatchCommitStatus.Conflict,
                    item.Decision,
                    conflict)));
            return new PatchCommitResult(false, OrderResults(plan, resultItems), conflict);
        }

        for (var index = 0; index < commitItems.Count; index++)
        {
            var item = commitItems[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyCommitItemAsync(item, cancellationToken);
                resultItems.Add(new PatchCommitFileResult(item.Plan.ReviewPath, PatchCommitStatus.Committed, item.Decision));
            }
            catch (Exception ex)
            {
                var error = $"Failed to apply '{item.Plan.ReviewPath}': {ex.Message}";
                resultItems.Add(new PatchCommitFileResult(item.Plan.ReviewPath, PatchCommitStatus.Failed, item.Decision, error));
                for (var remaining = index + 1; remaining < commitItems.Count; remaining++)
                {
                    resultItems.Add(
                        new PatchCommitFileResult(
                            commitItems[remaining].Plan.ReviewPath,
                            PatchCommitStatus.NotAttempted,
                            commitItems[remaining].Decision,
                            "Not attempted because a previous file operation failed."));
                }

                return new PatchCommitResult(false, OrderResults(plan, resultItems), error);
            }
        }

        return new PatchCommitResult(true, OrderResults(plan, resultItems));
    }

    private static PatchCommitFileResult[] OrderResults(PatchPlan plan, IEnumerable<PatchCommitFileResult> results)
    {
        var order = plan.Files
            .Select((file, index) => (file.ReviewPath, Index: index))
            .ToDictionary(static item => item.ReviewPath, static item => item.Index, PathUtilities.SystemPathComparer);
        return results
            .AsValueEnumerable()
            .OrderBy(result => order.TryGetValue(result.Path, out var index) ? index : int.MaxValue)
            .ToArray();
    }

    private static byte[] EncodeAcceptedContent(PatchPlanFile file, string content, PatchLimits limits)
    {
        var contentBytes = file.Original.Encode(content);
        PatchPlanBuilder.EnsureOutputBudget(
            file.ReviewPath,
            file.Original.Content,
            content,
            contentBytes.Length,
            limits);
        return contentBytes;
    }

    private static async ValueTask<string?> VerifyCurrentStateAsync(
        IReadOnlyList<CommitItem> items,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is AddCommitItem add)
            {
                var addConflict = VerifyResolution(add.Add.SourceResolution, PathResolutionMode.PreserveFinalComponent, "destination");
                if (addConflict is not null) return addConflict;
                if (EntryExists(add.Add.SourcePath)) return $"The patch destination '{add.Add.SourcePath}' was created while awaiting approval.";
                continue;
            }

            if (item is UpdateCommitItem update)
            {
                var updateConflict = VerifyResolution(update.Update.SourceResolution, PathResolutionMode.FollowFinalComponent, "source");
                if (updateConflict is not null) return updateConflict;
                var fingerprintConflict = await VerifyFingerprintAsync(update.Update.SourcePath, update.Update.Original, limits, cancellationToken);
                if (fingerprintConflict is not null) return fingerprintConflict;
                continue;
            }

            if (item is DeleteCommitItem delete)
            {
                var deleteConflict = VerifyResolution(delete.Delete.SourceResolution, PathResolutionMode.PreserveFinalComponent, "source entry");
                if (deleteConflict is not null) return deleteConflict;
                var entryConflict = VerifySourceEntry(delete.Delete.SourcePath, delete.Delete.SourceLink);
                if (entryConflict is not null) return entryConflict;
                if (delete.Delete.SourceLink is null)
                {
                    var fingerprintConflict = await VerifyFingerprintAsync(delete.Delete.SourcePath, delete.Delete.Original, limits, cancellationToken);
                    if (fingerprintConflict is not null) return fingerprintConflict;
                }

                continue;
            }

            if (item is MoveCommitItem move)
            {
                var sourceConflict = VerifyResolution(move.Move.SourceResolution, PathResolutionMode.PreserveFinalComponent, "source entry");
                if (sourceConflict is not null) return sourceConflict;
                var sourceEntryConflict = VerifySourceEntry(move.Move.SourcePath, move.Move.SourceLink);
                if (sourceEntryConflict is not null) return sourceEntryConflict;

                var contentConflict = VerifyResolution(move.Move.ContentResolution, PathResolutionMode.FollowFinalComponent, "content source");
                if (contentConflict is not null) return contentConflict;
                var fingerprintConflict = await VerifyFingerprintAsync(move.Move.ContentPath, move.Move.Original, limits, cancellationToken);
                if (fingerprintConflict is not null) return fingerprintConflict;

                var destinationConflict = VerifyResolution(move.Move.DestinationResolution, PathResolutionMode.PreserveFinalComponent, "destination");
                if (destinationConflict is not null) return destinationConflict;
                if (EntryExists(move.Move.DestinationPath)) return $"The patch destination '{move.Move.DestinationPath}' was created while awaiting approval.";
                continue;
            }

            return $"The patch commit type '{item.GetType().Name}' cannot be verified.";
        }

        return null;
    }

    private static string? VerifyResolution(PathResolutionResult planned, PathResolutionMode mode, string role)
    {
        var current = PathUtilities.ResolvePath(planned.RequestedPath, mode);
        if (!current.IsSuccess)
        {
            var failure = current.Failure;
            return failure is null ?
                $"The patch {role} '{planned.RequestedPath}' could not be resolved while awaiting approval." :
                $"The patch {role} '{planned.RequestedPath}' could not be resolved at '{failure.Path}' ({failure.Kind}): {failure.Message}";
        }

        if (!string.Equals(current.ResolvedPath, planned.ResolvedPath, PathUtilities.SystemPathComparison) ||
            !HaveSameLinkTransitions(current.LinkTransitions, planned.LinkTransitions))
        {
            return $"The patch {role} '{planned.RequestedPath}' resolves differently than it did during review. " +
                $"Planned path: '{planned.ResolvedPath}'. Current path: '{current.ResolvedPath}'. No changes were written.";
        }

        return null;
    }

    private static string? VerifySourceEntry(string path, PatchLinkSnapshot? plannedLink)
    {
        try
        {
            var entry = PathUtilities.InspectEntry(path);
            if (plannedLink is not null)
            {
                return entry is { Kind: PathUtilities.EntryKind.Link, RawLinkTarget: { } currentTarget } &&
                    string.Equals(currentTarget, plannedLink.Target, StringComparison.Ordinal) ?
                    null :
                    $"The patch source link '{path}' changed while awaiting approval. Planned target: '{plannedLink.Target}'. " +
                    $"Current entry: '{DescribeEntry(entry)}'.";
            }

            return entry.Kind switch
            {
                PathUtilities.EntryKind.Regular => null,
                PathUtilities.EntryKind.Missing => $"The patch source entry '{path}' was removed while awaiting approval.",
                PathUtilities.EntryKind.Link => $"The patch source entry '{path}' became a link to '{entry.RawLinkTarget}' while awaiting approval.",
                PathUtilities.EntryKind.UnsupportedReparsePoint =>
                    $"The patch source entry '{path}' became an unsupported reparse point while awaiting approval.",
                _ => $"The patch source entry '{path}' has an unsupported entry kind '{entry.Kind}'."
            };
        }
        catch (IOException ex)
        {
            return $"The patch source entry '{path}' could not be verified: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Access to patch source entry '{path}' was denied during verification: {ex.Message}";
        }
    }

    private static async ValueTask<string?> VerifyFingerprintAsync(
        string path,
        PatchTextFileSnapshot original,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = PathUtilities.InspectEntry(path);
            if (entry.Kind is PathUtilities.EntryKind.Missing)
            {
                return $"The patch content source '{path}' was removed while awaiting approval.";
            }

            if (entry.Kind is PathUtilities.EntryKind.Link)
            {
                return $"The resolved patch content source '{path}' became a link to '{entry.RawLinkTarget}' while awaiting approval.";
            }

            if (entry.Kind is PathUtilities.EntryKind.UnsupportedReparsePoint)
            {
                return $"The resolved patch content source '{path}' became an unsupported reparse point while awaiting approval.";
            }

            var currentBytes = await ReadRawBytesAsync(path, limits.MaxFileBytes, cancellationToken);
            return original.HasSameFingerprint(currentBytes) ?
                null :
                $"The patch content source '{path}' changed while awaiting approval. No changes were written.";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return $"The patch content source '{path}' was removed while awaiting approval.";
        }
        catch (IOException ex)
        {
            return $"The patch content source '{path}' could not be verified: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Access to patch content source '{path}' was denied during verification: {ex.Message}";
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            return PathUtilities.InspectEntry(path).Kind is not PathUtilities.EntryKind.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static async ValueTask ApplyCommitItemAsync(CommitItem item, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case UpdateCommitItem update:
                await OverwriteExistingFileAsync(update.Update.SourcePath, update.ContentBytes, cancellationToken);
                File.SetAttributes(update.Update.SourcePath, update.Update.Original.Attributes);
                break;
            case AddCommitItem add:
                await CreateNewFileAsync(add.Add.SourcePath, add.ContentBytes, cancellationToken);
                break;
            case DeleteCommitItem delete:
                File.Delete(delete.Delete.SourcePath);
                break;
            case MoveCommitItem move:
                await ApplyMoveAsync(move, cancellationToken);
                break;
            default:
                throw new PatchCommitException($"The patch commit type '{item.GetType().Name}' is not supported.");
        }
    }

    private static async ValueTask OverwriteExistingFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous);
        stream.SetLength(0);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask CreateNewFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask ApplyMoveAsync(MoveCommitItem move, CancellationToken cancellationToken)
    {
        var isDestinationCreated = false;
        try
        {
            {
                await using var stream = new FileStream(
                    move.Move.DestinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous);
                isDestinationCreated = true;
                await stream.WriteAsync(move.ContentBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.SetAttributes(move.Move.DestinationPath, move.Move.Original.Attributes);
            File.Delete(move.Move.SourcePath);
        }
        catch (Exception ex) when (isDestinationCreated)
        {
            throw new IOException(
                $"The destination '{move.Move.DestinationPath}' was created, but the move could not finish and the source entry " +
                $"'{move.Move.SourcePath}' was not removed. " +
                "The move is partially applied and requires manual cleanup.",
                ex);
        }
    }

    private static bool HaveSameLinkTransitions(
        IReadOnlyList<PathLinkTransition> current,
        IReadOnlyList<PathLinkTransition> planned)
    {
        if (current.Count != planned.Count) return false;
        for (var index = 0; index < current.Count; index++)
        {
            if (!string.Equals(current[index].Path, planned[index].Path, PathUtilities.SystemPathComparison) ||
                !string.Equals(current[index].LinkTarget, planned[index].LinkTarget, StringComparison.Ordinal) ||
                !string.Equals(current[index].ExpandedTargetPath, planned[index].ExpandedTargetPath, PathUtilities.SystemPathComparison))
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeEntry(PathUtilities.EntryInspection entry) => entry.Kind switch
    {
        PathUtilities.EntryKind.Missing => "missing",
        PathUtilities.EntryKind.Regular => "regular file-system entry",
        PathUtilities.EntryKind.Link => $"link to '{entry.RawLinkTarget}'",
        PathUtilities.EntryKind.UnsupportedReparsePoint => "unsupported reparse point",
        _ => entry.Kind.ToString()
    };

    private static async ValueTask<byte[]> ReadRawBytesAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        if (stream.Length > maxBytes || stream.Length > int.MaxValue)
        {
            throw new PatchCommitException($"The file '{path}' exceeds the patch size limit.");
        }

        var bytes = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }

        if (offset != bytes.Length) Array.Resize(ref bytes, offset);
        return bytes;
    }

    private abstract class CommitItem(PatchPlanFile plan, PatchFileDecision decision)
    {
        public PatchPlanFile Plan { get; } = plan;

        public PatchFileDecision Decision { get; } = decision;
    }

    private sealed class AddCommitItem(
        PatchAddPlanFile add,
        PatchContentFileDecision decision,
        byte[] contentBytes
    ) : CommitItem(add, decision)
    {
        public PatchAddPlanFile Add { get; } = add;

        public byte[] ContentBytes { get; } = contentBytes;
    }

    private sealed class UpdateCommitItem(
        PatchUpdatePlanFile update,
        PatchContentFileDecision decision,
        byte[] contentBytes
    ) : CommitItem(update, decision)
    {
        public PatchUpdatePlanFile Update { get; } = update;

        public byte[] ContentBytes { get; } = contentBytes;
    }

    private sealed class DeleteCommitItem(
        PatchDeletePlanFile delete,
        PatchDeleteFileDecision decision
    ) : CommitItem(delete, decision)
    {
        public PatchDeletePlanFile Delete { get; } = delete;
    }

    private sealed class MoveCommitItem(
        PatchMovePlanFile move,
        PatchContentFileDecision decision,
        byte[] contentBytes
    ) : CommitItem(move, decision)
    {
        public PatchMovePlanFile Move { get; } = move;

        public byte[] ContentBytes { get; } = contentBytes;
    }
}

/// <summary>
/// Reports an invalid or incomplete commit decision set.
/// </summary>
public sealed class PatchCommitException(string message) : InvalidOperationException(message);