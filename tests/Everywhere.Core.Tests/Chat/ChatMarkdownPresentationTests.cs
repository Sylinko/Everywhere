using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Views;
using System.Reflection;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatMarkdownPresentationTests
{
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

    private static Task GetParseTask(ChatMarkdownPresentation presentation)
    {
        var field = typeof(ChatMarkdownPresentation).GetField("_parseTask", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(presentation) as Task ?? throw new AssertionException("Expected an active parse task.");
    }
}