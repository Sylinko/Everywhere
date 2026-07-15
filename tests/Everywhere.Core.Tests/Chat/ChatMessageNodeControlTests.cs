using Everywhere.Chat;
using Everywhere.Views;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public class ChatMessageNodeControlTests
{
    [Test]
    public void Node_IsAdaptedWithoutReplacingOuterDataContext()
    {
        var outerDataContext = new object();
        var message = new UserChatMessage("Hello", []);
        var node = new ChatMessageNode(message);
        var control = new ChatMessageNodeControl { DataContext = outerDataContext, Node = node };
        var messageControl = (ChatMessageControl)control.Child!;

        Assert.Multiple(() =>
        {
            Assert.That(control.DataContext, Is.SameAs(outerDataContext));
            Assert.That(messageControl.DataContext, Is.SameAs(outerDataContext));
            Assert.That(messageControl.Content, Is.SameAs(message));
        });
    }
}
