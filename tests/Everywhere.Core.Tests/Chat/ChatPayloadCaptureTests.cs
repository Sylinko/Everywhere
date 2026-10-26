using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Everywhere.Serialization;
using Lucide.Avalonia;
using MessagePack;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatPayloadCaptureTests
{
    [Test]
    public void Capture_WhenPayloadOwnsChildChat_CollectsNestedFilesAndPreservesEmbeddedWireFormat()
    {
        var child = new ChatData();
        var file = new FileAttachment(new DirectLocaleKey("nested"), "before.txt", "nested-file", "text/plain");
        var user = child.Add(new UserChatMessage("child", [file]));
        var alternative = child.CreateBranchOn(user, new UserChatMessage("alternative", []));
        child.SelectChild(child.RootNode, user);
        using var tool = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("tool"));
        tool.DisplaySink.AppendSubagent(child);

        var capture = ChatPayloadCapture.Capture(tool);
        file.FilePath = "after.txt";
        using var restored = (FunctionCallChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(capture.Payload);
        var restoredChild = ((ChatPluginSubagentDisplayBlock)restored.DisplayBlocks.Single()).ChatData;
        var restoredFile = ((UserChatMessage)restoredChild.Items[1].Message).Attachments.OfType<FileAttachment>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(restoredChild.Items[1].Id, Is.EqualTo(user.Id));
            Assert.That(restoredChild.FindNode(alternative.Id), Is.Not.Null);
            Assert.That(restoredFile.FilePath, Is.EqualTo("before.txt"));
            Assert.That(capture.Files.Single().FilePath, Is.EqualTo(restoredFile.FilePath));
            Assert.That(capture.Files.Single(), Is.Not.SameAs(file));
        });
    }
}