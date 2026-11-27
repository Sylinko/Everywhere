using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using Everywhere.Chat;
using Everywhere.Views;
using LiveMarkdown.Avalonia;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatMarkdownPresentationTests
{
    [AvaloniaTest]
    public async Task DocumentUpdate_WhenAppending_PublishesChainedUtf16Ranges()
    {
        var source = new ChatTextBuffer().Append("one\n\n");
        using var presentation = new ChatMarkdownPresentation(source);
        await presentation.PrepareAsync(CancellationToken.None);
        Assert.That(presentation.DocumentUpdate, Is.TypeOf<MarkdownDocumentUpdate.Full>());

        foreach (var chunk in new[] { "⭐", "💬\n" })
        {
            var previous = presentation.DocumentUpdate ?? throw new AssertionException("Expected a previous update.");
            var start = source.Length;
            source.Append(chunk);
            await presentation.PrepareAsync(CancellationToken.None);

            var incremental = presentation.DocumentUpdate as MarkdownDocumentUpdate.Incremental;
            Assert.That(incremental, Is.Not.Null);
            Assert.That(Follows(incremental, previous), Is.True);
            Assert.That(incremental.Change,
                Is.EqualTo(new ObservableStringBuilderChangedEventArgs(start, chunk.Length, source.Length, source.Version)));
        }
    }

    [AvaloniaTest]
    public async Task DocumentUpdate_WhenAnExistingStreamIsCleared_PublishesAFullReplacement()
    {
        var source = new ChatTextBuffer().Append("old");
        using var presentation = new ChatMarkdownPresentation(source);
        await presentation.PrepareAsync(CancellationToken.None);
        source.Append(" pending");
        presentation.Refresh();
        source.Clear().Append("a replacement longer than the original");
        presentation.Refresh();

        Assert.That(presentation.DocumentUpdate, Is.Null);
        await presentation.PrepareAsync(CancellationToken.None);
        Assert.That(presentation.DocumentUpdate, Is.TypeOf<MarkdownDocumentUpdate.Full>());
        Assert.That(presentation.DocumentUpdate?.Version, Is.EqualTo(source.Version));
    }

    [AvaloniaTest]
    public async Task Renderer_WhenFirstReceivingAnIncrementalUpdate_RendersEarlierBlocksAndTail()
    {
        var source = new ChatTextBuffer().Append("first\n\nsecond");
        using var presentation = new ChatMarkdownPresentation(source);
        await presentation.PrepareAsync(CancellationToken.None);
        source.Append(" plus tail");
        await presentation.PrepareAsync(CancellationToken.None);
        Assert.That(presentation.DocumentUpdate, Is.TypeOf<MarkdownDocumentUpdate.Incremental>());

        using var other = new ChatMarkdownPresentation(new ChatTextBuffer().Append("stale\n\nsecond"));
        await other.PrepareAsync(CancellationToken.None);
        var fresh = new MarkdownRenderer();
        var recycled = new MarkdownRenderer { DocumentUpdate = other.DocumentUpdate };
        foreach (var renderer in new[] { fresh, recycled })
        {
            // The recycled renderer's old update even has the same numeric base version.
            renderer.DocumentUpdate = presentation.DocumentUpdate;
            var text = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Select(block => block.ActualText);
            Assert.That(text, Is.EqualTo(new[] { "first", "second plus tail" }));
        }
    }

    [AvaloniaTest]
    public async Task Refresh_WhenTokensArriveInABurst_CoalescesAndFlushesTheTailWithoutPreparation()
    {
        var source = new ChatTextBuffer().Append("start");
        using var presentation = new ChatMarkdownPresentation(source);
        await presentation.PrepareAsync(CancellationToken.None);
        var caughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        presentation.PropertyChanged += (_, _) =>
        {
            commits++;
            if (presentation.DocumentUpdate?.Version == source.Version)
            {
                caughtUp.TrySetResult();
            }
        };

        for (var i = 0; i < 50; i++)
        {
            source.Append(" x");
            presentation.Refresh();
        }

        await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(commits, Is.InRange(1, 2));
        var renderer = new MarkdownRenderer { DocumentUpdate = presentation.DocumentUpdate };
        Assert.That(renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Single().ActualText, Is.EqualTo(source.ToString()));
    }

    [AvaloniaTest]
    public async Task Refresh_WhenEveryCommitReceivesMoreText_PacesConsecutiveCommits()
    {
        var source = new ChatTextBuffer().Append("start");
        using var presentation = new ChatMarkdownPresentation(source);
        var timestamps = new List<long>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presentation.PropertyChanged += (_, _) =>
        {
            if (presentation.DocumentUpdate is null)
            {
                return;
            }

            timestamps.Add(Stopwatch.GetTimestamp());
            if (timestamps.Count == 5)
            {
                finished.TrySetResult();
                return;
            }

            source.Append(" x");
            presentation.Refresh();
        };

        presentation.Refresh();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Four nominal 33 ms intervals, with room for timer rounding and first-callback jitter.
        Assert.That(Stopwatch.GetElapsedTime(timestamps[0], timestamps[^1]), Is.GreaterThan(TimeSpan.FromMilliseconds(75)));
        Assert.That(presentation.DocumentUpdate?.Version, Is.EqualTo(source.Version));
    }

    [AvaloniaTest]
    public async Task Prepare_WhenStreamingRefreshIsScheduled_StartsTheParseWithoutWaitingForATick()
    {
        var source = new ChatTextBuffer().Append("first");
        using var presentation = new ChatMarkdownPresentation(source);
        await presentation.PrepareAsync(CancellationToken.None);
        var previousParse = GetParseTask(presentation);
        source.Append(" second");
        presentation.Refresh();

        var prepared = presentation.PrepareAsync(CancellationToken.None);
        // The UI dispatcher hasn't yielded to a timer between Refresh and this assertion.
        Assert.That(GetParseTask(presentation), Is.Not.SameAs(previousParse));
        await prepared.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(presentation.DocumentUpdate?.Version, Is.EqualTo(source.Version));
    }

    [AvaloniaTest]
    public async Task Prepare_WhenOutputKeepsAppending_CompletesForEntryVersion()
    {
        var source = new ChatTextBuffer().Append("first");
        using var presentation = new ChatMarkdownPresentation(source);
        var targetVersion = source.Version;
        presentation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMarkdownPresentation.DocumentUpdate) && presentation.DocumentUpdate is not null)
            {
                source.Append(" next");
            }
        };

        await presentation.PrepareAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(presentation.DocumentUpdate?.Version, Is.GreaterThanOrEqualTo(targetVersion));
        Assert.That(presentation.DocumentUpdate?.Version, Is.LessThan(source.Version));
    }

    [AvaloniaTest]
    public async Task Prepare_WhenActiveParseIsOlder_WaitsForRequiredVersion()
    {
        var source = new ChatTextBuffer().Append("first");
        using var presentation = new ChatMarkdownPresentation(source);
        presentation.Refresh();
        source.Append(" second");
        var targetVersion = source.Version;

        await presentation.PrepareAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(presentation.DocumentUpdate?.Version, Is.EqualTo(targetVersion));
    }

    [AvaloniaTest]
    public async Task Prepare_WhenOneWaitIsCanceled_ReusesVisibleParseAndCommitsForRemainingWaiter()
    {
        var source = new ChatTextBuffer().Append("first");
        using var presentation = new ChatMarkdownPresentation(source);
        using var cancellation = new CancellationTokenSource();
        presentation.Refresh();
        var parse = GetParseTask(presentation);
        var canceledWait = presentation.PrepareAsync(cancellation.Token);
        var retainedWait = presentation.PrepareAsync(CancellationToken.None);
        presentation.Refresh();
        Assert.That(GetParseTask(presentation), Is.SameAs(parse));
        cancellation.Cancel();

        await Assert.ThatAsync(async () => await canceledWait, Throws.InstanceOf<OperationCanceledException>());
        await retainedWait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(parse.IsCompletedSuccessfully, Is.True);
        Assert.That(presentation.DocumentUpdate?.Version, Is.EqualTo(source.Version));
    }

    [AvaloniaTest]
    public async Task Prepare_WhenSourceIsCleared_RejectsOldEpochAndPreparesReplacement()
    {
        var source = new ChatTextBuffer().Append("old");
        using var presentation = new ChatMarkdownPresentation(source);
        var acceptedVersions = new List<long>();
        presentation.PropertyChanged += (_, _) =>
        {
            if (presentation.DocumentUpdate is { } update)
            {
                acceptedVersions.Add(update.Version);
            }
        };
        var prepared = presentation.PrepareAsync(CancellationToken.None);
        source.Clear().Append("replacement");
        presentation.Refresh();

        await prepared.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(acceptedVersions, Is.EqualTo(new[] { source.Version }));
        Assert.That(presentation.DocumentUpdate?.Version, Is.EqualTo(source.Version));
    }

    [AvaloniaTest]
    public async Task Dispose_WhenParsing_DiscardsResultAndStopsCatchUp()
    {
        var source = new ChatTextBuffer().Append("old");
        using var presentation = new ChatMarkdownPresentation(source);
        presentation.Refresh();
        var parse = GetParseTask(presentation);
        presentation.Dispose();
        source.Append(" new");
        await parse.WaitAsync(TimeSpan.FromSeconds(5));
        presentation.Refresh();

        Assert.That(presentation.DocumentUpdate, Is.Null);
    }

    [AvaloniaTest]
    public async Task ChatLinkRenderer_DefaultStreamingReveal_EnablesCharacterModeAndActivatesOnIncrementalAppend()
    {
        var linkRenderer = new ChatLinkRenderer();
        var markdownRenderer = new ChatMarkdownRenderer();
        var plainRenderer = new MarkdownRenderer();
        var source = new ChatTextBuffer().Append("initial");
        using var presentation = new ChatMarkdownPresentation(source);
        await presentation.PrepareAsync(CancellationToken.None);

        linkRenderer.DocumentUpdate = presentation.DocumentUpdate;
        var host = new StackPanel
        {
            Children = { linkRenderer, markdownRenderer, plainRenderer }
        };
        var window = new Window { Width = 360, Height = 200, Content = host };
        window.Styles.Add(new StyleInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Assets/Styles.axaml")
        });
        try
        {
            window.Show();
            Assert.That(linkRenderer.Classes.Contains("ChatLink"), Is.True);
            Assert.That(linkRenderer.StreamingRevealMode, Is.EqualTo(MarkdownStreamingRevealMode.Character));
            Assert.That(markdownRenderer.Classes.Contains("ChatLink"), Is.True);
            Assert.That(markdownRenderer.StreamingRevealMode, Is.EqualTo(MarkdownStreamingRevealMode.Character));
            Assert.That(plainRenderer.StreamingRevealMode, Is.EqualTo(MarkdownStreamingRevealMode.None));

            source.Append(" streaming delta\nsecond line");
            await presentation.PrepareAsync(CancellationToken.None);
            linkRenderer.DocumentUpdate = presentation.DocumentUpdate;
            window.UpdateLayout();

            var textBlock = linkRenderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Single();
            Assert.That(textBlock.StreamingRevealMode, Is.EqualTo(MarkdownStreamingRevealMode.Character));
            Assert.That(ElementComposition.GetElementChildVisual(textBlock), Is.Not.Null);
            var snapshot = typeof(MarkdownTextBlock)
                .GetProperty("ActiveRevealSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(textBlock);
            Assert.That(snapshot, Is.Not.Null);
        }
        finally
        {
            window.Close();
        }
    }

    private static Task GetParseTask(ChatMarkdownPresentation presentation)
    {
        var field = typeof(ChatMarkdownPresentation).GetField("_parseTask", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(presentation) as Task ?? throw new AssertionException("Expected an active parse task.");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Follows")]
    private static extern bool Follows(MarkdownDocumentUpdate.Incremental update, MarkdownDocumentUpdate previous);
}
