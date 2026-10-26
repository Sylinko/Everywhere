using Avalonia.Controls;
using Everywhere.Chat;
using Everywhere.Views;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public class ChatMessageControlTests
{
    [Test]
    public async Task Snapshot_WhenRowRefreshes_PreservesControlAndOuterDataContext()
    {
        var outerDataContext = new object();
        var message = new UserChatMessage("Hello", []);
        await using var context = new RootChatContext(new ChatData(), null);
        var node = context.Data.Add(message);
        var row = new ChatMessagePresentationRow(context, node);
        row.Refresh();
        var messageControl = new ChatMessageControlTemplate().Build(row) as ChatMessageControl
            ?? throw new AssertionException("Expected a message control for the row.");
        var host = new Decorator { DataContext = outerDataContext, Child = messageControl };

        Assert.Multiple(() =>
        {
            Assert.That(messageControl.Row, Is.SameAs(row));
            Assert.That(messageControl.DataContext, Is.SameAs(outerDataContext));
            Assert.That(messageControl.Content, Is.EqualTo(new ChatUserState("Hello", message.Attachments)));
        });

        message.Content = "Updated";
        Assert.That(((ChatUserState)messageControl.Content).Content, Is.EqualTo("Hello"));
        row.Refresh();
        Assert.That(((ChatUserState)messageControl.Content).Content, Is.EqualTo("Updated"));
        Assert.That(host.Child, Is.SameAs(messageControl));
        Assert.That(messageControl.DataContext, Is.SameAs(outerDataContext));
    }
}