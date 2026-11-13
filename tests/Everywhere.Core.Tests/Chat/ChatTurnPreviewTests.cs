using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Everywhere.Chat;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Storage;
using Everywhere.Views;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatTurnPreviewTests
{
    [AvaloniaTest]
    public async Task Preview_WhenSyncAddsColdAlternative_ReloadsWithoutReplacingEntryOrControl()
    {
        using var database = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(database.Factory);
        using var source = new ChatData();
        source.Add(new UserChatMessage("question", []));
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageTextSpan("original"));
        var original = source.Add(assistant);
        await storage.SaveDataAsync(source);
        await using var context = await storage.LoadContextAsync(source.Metadata.Id);
        using var index = new ChatTurnNavigationIndex(context);
        var entry = index.Turns.Single();
        var preview = new ChatTurnPreview { Entry = entry };
        var answer = preview.FindControl<TextBlock>("Answer") ?? throw new AssertionException("Missing preview answer.");
        var window = new Window { Content = preview };
        try
        {
            window.Show();
            await WaitForAnswerAsync("original");
            Assert.That(answer.Text, Is.EqualTo("original"));

            var alternative = new AssistantChatMessage();
            alternative.AddSpan(new AssistantChatMessageTextSpan("synced alternative"));
            var added = source.CreateBranchOn(original, alternative);
            await storage.SaveDataAsync(source);
            var topology = source.GetAllNodes().Select(node => new ChatNodeTopology(
                node.Id,
                node.Parent?.Id,
                node.Turn?.Id,
                node.ChoiceChildId,
                node.MessageKind,
                node.SummaryThroughNodeId,
                node.CreatedAt,
                node.DateModified)).ToArray();

            // The receive path installs new topology without eagerly loading alternative bodies.
            await context.DispatchAsync(() =>
            {
                context.Data.ApplyCommittedTopology(topology, false);
                Assert.That(context.Data.FindNode(added.Id)?.IsMessageLoaded, Is.False);
            });
            await WaitForAnswerAsync("synced alternative");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(index.Turns.Single(), Is.SameAs(entry));
                Assert.That(preview.Entry, Is.SameAs(entry));
                Assert.That(answer.Text, Is.EqualTo("synced alternative"));
            }
        }
        finally
        {
            window.Close();
        }

        async Task WaitForAnswerAsync(string expected)
        {
            // Observe the result instead of loading on the control's behalf: the card must
            // initiate and complete loading while preserving its existing visual identity.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await answer.GetObservable(TextBlock.TextProperty).Where(text => text == expected).Take(1).ToTask(timeout.Token);
        }
    }
}