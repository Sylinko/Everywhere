using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Everywhere.Patches.SemanticKernel.Tests;

public sealed class ChatResponseUpdatePatchTests
{
    [Test]
    public void ToStreamingContent_WithFinishReasonAndUsage_PreservesBothMetadataValues()
    {
        var update = new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            FinishReason = ChatFinishReason.Length,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["thoughtSignature"] = "signature" },
            Contents = [new UsageContent(new UsageDetails { InputTokenCount = 12 })]
        };
        var type = typeof(Kernel).Assembly.GetType("Microsoft.Extensions.AI.ChatResponseUpdateExtensions")
            ?? throw new AssertionException("Missing woven conversion type.");
        var method = type.GetMethod("ToStreamingChatMessageContent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(ChatResponseUpdate)]) ?? throw new AssertionException("Missing woven conversion method.");
        var content = method.Invoke(null, [update]) as StreamingChatMessageContent
            ?? throw new AssertionException("Missing streaming content.");
        Assert.Multiple(() =>
        {
            Assert.That(content.Metadata?["FinishReason"]?.ToString(), Is.EqualTo("length"));
            Assert.That(content.Metadata?["Usage"], Is.TypeOf<UsageContent>());
            Assert.That(content.Metadata?["thoughtSignature"], Is.EqualTo("signature"));
            Assert.That(update.AdditionalProperties.ContainsKey("FinishReason"), Is.False);
        });
    }
}
