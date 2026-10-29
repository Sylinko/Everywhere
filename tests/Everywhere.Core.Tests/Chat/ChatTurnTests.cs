using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Everywhere.Serialization;
using Lucide.Avalonia;
using MessagePack;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatTurnTests
{
    [AvaloniaTest]
    public void Restore_WhenSiblingTimesTie_SortsByUuidAndPreservesLegacyChoice()
    {
        var time = DateTimeOffset.UtcNow.AddDays(1);
        var firstId = Guid.Parse("00000001-0000-0000-8000-000000000000");
        var secondId = Guid.Parse("00000100-0000-0000-8000-000000000000");
        var message = new UserChatMessage("request", []);
        var first = new ChatMessageNode(firstId, ChatMessageKind.User, time, time);
        var second = new ChatMessageNode(secondId, ChatMessageKind.User, time, time);

        // Exercise the old index-based wire representation, not a test-only construction API.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(4);
        MessagePackSerializer.Serialize(ref writer, Guid.NewGuid());
        MessagePackSerializer.Serialize<ChatMessage>(ref writer, new RootChatMessage());
        MessagePackSerializer.Serialize(ref writer, new[] { secondId, firstId });
        writer.Write(0);
        writer.Flush();
        var root = MessagePackSerializer.Deserialize<ChatMessageNode>(buffer.WrittenMemory);
        using var data = new ChatData(new ChatMetadata(Guid.NewGuid(), time, time, null), [second, first], root);

        Assert.Multiple(() =>
        {
            Assert.That(root.Children, Is.EqualTo(new[] { firstId, secondId }));
            Assert.That(root.ChoiceChildId, Is.EqualTo(secondId));
            Assert.That(root.ChoiceIndex, Is.EqualTo(1));
            Assert.That(data.Items[^1], Is.SameAs(second));
        });

        // A delayed message is inserted by its creation time, without stealing the selection.
        var delayed = data.Append(root, message, shouldSelectChild: false);
        Assert.That(root.Children[0], Is.EqualTo(delayed.Id));
        Assert.That(root.ChoiceChildId, Is.EqualTo(secondId));
    }

    [AvaloniaTest]
    public void CompressionOutcome_WhenSaveCapturesAtNotification_PersistsTheCompleteOutcome()
    {
        foreach (var outcome in new[] { "Complete", "Cancel", "Fail" })
        {
            using var data = new ChatData();
            var user = data.Add(new UserChatMessage("question", []));
            var message = new ContextCompressionChatMessage(user.Id, "model", ContextCompressionTrigger.Manual, null, null);
            var node = data.Add(message);
            data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());
            var captures = new List<ChatPayloadCapture>();
            data.DurableChanged += (_, _) =>
            {
                // Save and acknowledge at the notification boundary, before the caller resumes.
                var pending = data.CapturePendingWrites();
                captures.Add(ChatPayloadCapture.Capture(node.Message));
                data.PersistenceDirtyState.Acknowledge(pending.Select(item => item.Change));
            };
            var finishedAt = DateTimeOffset.UtcNow;
            switch (outcome)
            {
                case "Complete":
                    message.Complete("summary", true, finishedAt);
                    break;
                case "Cancel":
                    message.Cancel(finishedAt);
                    break;
                case "Fail":
                    message.Fail(new DirectLocaleKey("failed"), finishedAt);
                    break;
            }

            Assert.That(captures, Has.Count.EqualTo(1), outcome);
            var saved = (ContextCompressionChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(captures[0].Payload);
            Assert.Multiple(() =>
            {
                Assert.That(saved.FinishedAt, Is.EqualTo(finishedAt), outcome);
                Assert.That(saved.HasSummary, Is.EqualTo(outcome == "Complete"), outcome);
                Assert.That(saved.WasSourceHistoryTrimmed, Is.EqualTo(outcome == "Complete"), outcome);
                Assert.That(saved.IsCanceled, Is.EqualTo(outcome == "Cancel"), outcome);
                Assert.That(saved.ErrorMessageKey?.ToString(), Is.EqualTo(outcome == "Fail" ? "failed" : null), outcome);
                Assert.That(captures[0].SummaryThroughNodeId, Is.EqualTo(outcome == "Complete" ? user.Id : (Guid?)null), outcome);
                Assert.That(node.SummaryThroughNodeId, Is.EqualTo(captures[0].SummaryThroughNodeId), outcome);
                Assert.That(message.IsBusy, Is.False, outcome);
                Assert.That(data.PersistenceDirtyState.HasChanges, Is.False, outcome);
            });
        }
    }

    [AvaloniaTest]
    public void CapturePendingWrites_WhenAppendFollowsCapture_KeepsTopologyAndAcknowledgmentInTheirBatch()
    {
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("question", []));
        var captured = data.CapturePendingWrites();
        var assistant = data.Append(user, new AssistantChatMessage());

        Assert.That(captured.Single(item => item.Topology.Id == user.Id).Topology.ChoiceChildId, Is.Null);
        Assert.That(captured.Select(item => item.Topology.Id), Does.Not.Contain(assistant.Id));
        data.PersistenceDirtyState.Acknowledge(captured.Select(item => item.Change));

        var next = data.CapturePendingWrites();
        Assert.That(next.Single(item => item.Topology.Id == user.Id).Topology.ChoiceChildId, Is.EqualTo(assistant.Id));
        Assert.That(next.Single(item => item.Topology.Id == assistant.Id).Change.Kind, Is.EqualTo(ChatNodeChangeKind.All));
        Assert.That(next.Select(item => item.Topology.Id), Does.Not.Contain(data.RootNode.Id));
    }

    [AvaloniaTest]
    public async Task RuntimeActivity_WhenCompleted_InvalidatesTurnWithoutDirtyingPayload()
    {
        using var data = new ChatData();
        data.Add(new UserChatMessage("work", []));
        var node = data.Add(new AssistantChatMessage());
        data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());
        var revision = data.SelectedTurns.Single().Revision;

        var activity = await Task.Run(() => data.StartActivity(LucideIconKind.Server, new DirectLocaleKey("Starting"), false));
        await Task.Run(() =>
        {
            activity.HeaderKey = new DirectLocaleKey("Ready");
            activity.Dispose();
        });

        Assert.Multiple(() =>
        {
            Assert.That(node.Turn?.Activities, Has.Count.EqualTo(1));
            Assert.That(node.Turn?.Revision, Is.GreaterThan(revision));
            Assert.That(activity.IsRunning, Is.False);
            Assert.That(data.PersistenceDirtyState.HasChanges, Is.False);
        });
    }

    [AvaloniaTest]
    public void NestedTool_WhenPreviewChanges_InvalidatesDirectOwnerWithoutPersistingRuntimeState()
    {
        using var data = new ChatData();
        data.Add(new UserChatMessage("work", []));
        var assistant = new AssistantChatMessage();
        var tool = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"));
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(tool));
        var node = data.Add(assistant);
        data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());
        var revision = data.SelectedTurns.Single().Revision;
        var slot = tool.RegisterActivityPresentation("preview");

        slot.Preview = new ChatPluginTextActivityPreview(new DirectLocaleKey("Working"));

        Assert.Multiple(() =>
        {
            Assert.That(tool.Owner, Is.SameAs(node));
            Assert.That(node.Turn?.Revision, Is.GreaterThan(revision));
            Assert.That(data.PersistenceDirtyState.HasChanges, Is.False);
        });
        tool.UnregisterActivityPresentation("preview", slot);
    }

    [AvaloniaTest]
    public void NestedProgress_WhenReported_ChangesModelSynchronouslyAndMarksOwningNode()
    {
        using var data = new ChatData();
        data.Add(new UserChatMessage("work", []));
        var assistant = new AssistantChatMessage();
        var tool = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"));
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(tool));
        var node = data.Add(assistant);
        var container = new ChatPluginContainerDisplayBlock();
        tool.DisplaySink.AppendBlock(container);
        var reporter = container.DisplaySink.AppendProgress(new DirectLocaleKey("Progress"));
        data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());

        reporter.Report(double.NaN);

        Assert.Multiple(() =>
        {
            Assert.That(((ChatPluginProgressDisplayBlock)container.Children[0]).Progress, Is.NaN);
            Assert.That(data.PersistenceDirtyState.Capture().Select(change => change.Node), Is.EqualTo(new[] { node }));
        });
    }

    [AvaloniaTest]
    public void EmbeddedChild_WhenContentChanges_DirtiesParentButRuntimeActivityDoesNot()
    {
        using var data = new ChatData();
        data.Add(new UserChatMessage("parent", []));
        var assistant = new AssistantChatMessage();
        var tool = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Child"));
        assistant.AddSpan(new AssistantChatMessageFunctionCallSpan(tool));
        var node = data.Add(assistant);
        var child = new ChatData();
        tool.DisplaySink.AppendSubagent(child);
        data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());

        child.Add(new UserChatMessage("child", []));
        child.Add(new AssistantChatMessage());
        Assert.That(data.PersistenceDirtyState.Capture().Select(change => change.Node), Is.EqualTo(new[] { node }));
        data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());
        using var activity = child.StartActivity(LucideIconKind.Server, new DirectLocaleKey("Working"), true);
        Assert.That(data.PersistenceDirtyState.HasChanges, Is.False);
    }

    [AvaloniaTest]
    public void RetryTruncation_WhenRemovedSourceWritesLate_DoesNotDirtyItsFormerOwner()
    {
        using var data = new ChatData();
        data.Add(new UserChatMessage("work", []));
        var assistant = new AssistantChatMessage();
        var span = new AssistantChatMessageTextSpan("provisional");
        assistant.AddSpan(span);
        data.Add(assistant);
        assistant.TruncateSpans(0);
        data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());

        span.ContentMarkdownBuilder.Append("late");
        Assert.That(span.Owner, Is.Null);
        Assert.That(data.PersistenceDirtyState.HasChanges, Is.False);
    }

    [AvaloniaTest]
    public void EditAndRetry_WhenSwitchingBranches_PreserveTurnAndNodeIdentities()
    {
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("original", []));
        var assistant = data.Add(new AssistantChatMessage());
        var nextUser = data.Add(new UserChatMessage("next", []));
        var nextAssistant = data.Add(new AssistantChatMessage());

        var retry = data.CreateBranchOn(assistant, new AssistantChatMessage());
        Assert.Multiple(() =>
        {
            Assert.That(retry.Turn, Is.SameAs(user.Turn));
            Assert.That(data.SelectedTurns, Is.EqualTo(new[] { user.Turn }));
            Assert.That(user.Turn?.SelectedNodes, Is.EqualTo(new[] { user, retry }));
        });

        var edited = data.CreateBranchOn(user, new UserChatMessage("edited", []));
        Assert.That(edited.Turn, Is.Not.SameAs(user.Turn));
        Assert.That(edited.Turn?.Id, Is.EqualTo(edited.Id));

        data.SelectChild(data.RootNode, user);
        Assert.That(data.Items[^1], Is.SameAs(retry));
        data.SelectChild(user, assistant);
        Assert.Multiple(() =>
        {
            Assert.That(data.Items[^1], Is.SameAs(nextAssistant));
            Assert.That(data.SelectedTurns, Is.EqualTo(new[] { user.Turn, nextUser.Turn }));
            Assert.That(data.FindTurn(edited.Id), Is.SameAs(edited.Turn));
            Assert.That(data.FindNode(retry.Id), Is.SameAs(retry));
        });
    }

    [AvaloniaTest]
    public void Append_WhenExistingChildrenAreUnselected_DoesNotReparentThem()
    {
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("request", []));
        var original = data.Add(new AssistantChatMessage());
        data.SelectChild(user, null);

        var appended = data.Add(new AssistantChatMessage());
        Assert.Multiple(() =>
        {
            Assert.That(original.Parent, Is.SameAs(user));
            Assert.That(appended.Parent, Is.SameAs(user));
            Assert.That(user.Children, Is.EquivalentTo(new[] { original.Id, appended.Id }));
            Assert.That(appended.Children, Is.Empty);
        });
    }

    [AvaloniaTest]
    public void Append_WhenExecutionBranchIsHidden_LeavesDisplayedBranchUnchanged()
    {
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("request", []));
        var running = data.Add(new AssistantChatMessage());
        var alternative = data.CreateBranchOn(running, new AssistantChatMessage());
        var revision = data.BranchRevision;

        var hiddenContinuation = data.Append(running, new AssistantChatMessage());
        Assert.Multiple(() =>
        {
            Assert.That(data.Items[^1], Is.SameAs(alternative));
            Assert.That(data.BranchRevision, Is.EqualTo(revision));
            Assert.That(data.GetPathTo(hiddenContinuation), Is.EqualTo(new[] { data.RootNode, user, running, hiddenContinuation }));
        });

        data.SelectChild(user, running);
        Assert.That(data.Items[^1], Is.SameAs(hiddenContinuation));
    }

    [AvaloniaTest]
    public void CreateBranch_WhenOriginalSiblingIsNoLongerSelected_DoesNotOverrideNewSelection()
    {
        using var data = new ChatData();
        var original = data.Add(new UserChatMessage("original", []));
        var selected = data.CreateBranchOn(original, new UserChatMessage("selected", []));
        var revision = data.BranchRevision;

        var delayedEdit = data.CreateBranchOn(original, new UserChatMessage("delayed edit", []));
        Assert.Multiple(() =>
        {
            Assert.That(data.Items[^1], Is.SameAs(selected));
            Assert.That(data.BranchRevision, Is.EqualTo(revision));
            Assert.That(delayedEdit.Parent, Is.SameAs(data.RootNode));
            Assert.That(data.FindTurn(delayedEdit.Id), Is.SameAs(delayedEdit.Turn));
        });
    }

    [AvaloniaTest]
    public void Continue_WhenCompressionFollowsAssistant_UsesTrailingNodeWithoutStartingTurn()
    {
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("request", []));
        var assistant = data.Add(new AssistantChatMessage());
        var compression = data.Add(new ContextCompressionChatMessage(
            assistant.Id, "model", ContextCompressionTrigger.Manual, null, null));

        Assert.That(data.ContinuationTarget, Is.SameAs(assistant));
        var tail = data.GetContinuationTail(assistant);
        var continued = data.Append(tail, new AssistantChatMessage());
        Assert.Multiple(() =>
        {
            Assert.That(tail, Is.SameAs(compression));
            Assert.That(continued.Turn, Is.SameAs(user.Turn));
            Assert.That(data.SelectedTurns, Has.Count.EqualTo(1));
            Assert.That(data.ContinuationTarget, Is.SameAs(continued));
            Assert.Throws<InvalidOperationException>(() => data.GetContinuationTail(assistant));
        });
    }

    [AvaloniaTest]
    public void ContinuationTarget_WhenBranchOrUserTurnChanges_FollowsSelectedPath()
    {
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("request", []));
        var assistant = data.Add(new AssistantChatMessage());
        var retry = data.CreateBranchOn(assistant, new AssistantChatMessage());
        Assert.That(data.ContinuationTarget, Is.SameAs(retry));
        Assert.Throws<InvalidOperationException>(() => data.GetContinuationTail(assistant));

        data.Add(new UserChatMessage("next", []));
        Assert.That(data.ContinuationTarget, Is.Null);
        Assert.Throws<InvalidOperationException>(() => data.GetContinuationTail(retry));

        data.SelectChild(user, assistant);
        Assert.That(data.ContinuationTarget, Is.SameAs(assistant));
        Assert.That(data.GetContinuationTail(assistant), Is.SameAs(assistant));
        Assert.Throws<InvalidOperationException>(() => data.GetContinuationTail(retry));
    }

    [AvaloniaTest]
    public void ContentChange_WhenAssistantStreams_InvalidatesTurnWithoutChangingPath()
    {
        using var data = new ChatData();
        data.Add(new UserChatMessage("request", []));
        var message = new AssistantChatMessage();
        var node = data.Add(message);
        var branchRevision = data.BranchRevision;
        var turnRevision = node.Turn?.Revision ?? throw new AssertionException("The assistant must belong to a turn.");

        data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());
        var modifiedAt = node.DateModified;
        message.IsBusy = true;
        message.Error = new IOException("runtime diagnostic");
        Assert.Multiple(() =>
        {
            Assert.That(data.PersistenceDirtyState.HasChanges, Is.False);
            Assert.That(node.DateModified, Is.EqualTo(modifiedAt));
            Assert.That(node.Turn?.Revision, Is.GreaterThan(turnRevision));
            Assert.That(data.BranchRevision, Is.EqualTo(branchRevision));
        });
    }

    [AvaloniaTest]
    public async Task SelectHistory_WhenVisualResetFollowsUser_DoesNotConsumeContextRound()
    {
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("keep this user", []));
        data.Add(new VisualContextResetChatMessage("reset notice", Guid.NewGuid()));
        var assistant = data.Add(new AssistantChatMessage());

        await using var runtime = new RootChatContext(data, null);
        var selection = await ChatHistoryBuilder.SelectContextMessagesAsync(runtime, data.GetPathTo(assistant), 0, 0);
        Assert.That(selection.Messages, Does.Contain(user.Message));
        Assert.That(selection.WasHistoryTrimmed, Is.False);
        Assert.That(data.SelectedTurns, Has.Count.EqualTo(1));
    }
}