using System.Buffers;
using Everywhere.Chat;
using Everywhere.Prompting.Documents;
using Everywhere.Serialization;
using MessagePack;

namespace Everywhere.Core.Tests.Chat;

public sealed class VisualElementAttachmentSerializationTests
{
    [Test]
    public void Content_WhenStructuredPromptIsSerialized_RoundTripsPolymorphically()
    {
        ChatAttachment source = new TextSelectionAttachment("selected text", null, null)
        {
            Content = new PromptCompactElement("Button").Attribute("id", 7).Flag("focused")
        };

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = (TextSelectionAttachment)MessagePackSerializer.Deserialize<ChatAttachment>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Content, Is.TypeOf<PromptCompactElement>());
            Assert.That(restored.Content?.ToString(), Is.EqualTo("<Button id=7 focused/>"));
        });
    }

    [Test]
    public void Content_WhenLegacyStringIsDeserialized_UpgradesToPromptText()
    {
        var bytes = MessagePackSerializer.Serialize("<Button id=7/>");
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));

        var restored = new VisualContextPromptMessagePackFormatter().Deserialize(ref reader, MessagePackSerializerOptions.Standard);
        var hasReachedEnd = reader.End;

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.TypeOf<PromptText>());
            Assert.That(restored?.ToString(), Is.EqualTo("<Button id=7/>"));
            Assert.That(hasReachedEnd, Is.True);
        });
    }
}
