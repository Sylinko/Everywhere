using Everywhere.Chat;
using Everywhere.Harness;
using Microsoft.Extensions.AI;

namespace Everywhere.Core.Tests.Harness;

public sealed class ResponseAssemblerTests
{
    [TestCase("R2", "A2")]
    [TestCase("\n", " ")]
    public void AppendUpdate_WhenInactiveChannelSendsEmptyStrings_PreservesContiguousBlocks(string reasoningTail, string answerTail)
    {
        var assembler = new ResponseAssembler("openai");

        // Some Chat Completions endpoints send both fields in every delta. MEAI emits text first.
        AppendDelta("", "R1");
        AppendDelta("", reasoningTail);
        AppendDelta("A1", "");
        AppendDelta(answerTail, "");

        Assert.That(
            assembler.Blocks.Select(block => (block.IsReasoning, block.Text?.ToString())),
            Is.EqualTo([(true, "R1" + reasoningTail), (false, "A1" + answerTail)]));

        void AppendDelta(string text, string reasoning)
        {
            assembler.AppendUpdate(new ChatResponseUpdate { Contents = [new TextContent(text), new TextReasoningContent(reasoning)] });
        }
    }

    [Test]
    public void Append_WhenReasoningHasNoText_KeepsItsStateOffThePreviousVisibleBlock()
    {
        var visible = new List<ChatTextBuffer>();
        var assembler = new ResponseAssembler(
            dialect: "anthropic",
            openVisibleBuffer: (_, _) =>
            {
                var buffer = new ChatTextBuffer();
                visible.Add(buffer);
                return buffer;
            });
        var before = assembler.Append(false, "before", null);
        assembler.AppendUpdate(new ChatResponseUpdate { Contents = [new TextReasoningContent(null) { ProtectedData = "first" }] });
        var opaque = assembler.Blocks[^1];
        assembler.EndBlock();
        var separate = assembler.Append(true, "summary", new Dictionary<string, object?> { [ReasoningMetadata.ProtectedDataKey] = "second" });
        assembler.Append(false, "after", null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(visible, Has.Count.EqualTo(3));
            Assert.That(visible[0], Is.SameAs(before.Text));
            Assert.That(before.Metadata.Count, Is.Zero);
            Assert.That(opaque.Text, Is.Null);
            Assert.That(opaque.Metadata[ReasoningMetadata.ProtectedDataKey], Is.EqualTo("first"));
            Assert.That(separate.Metadata[ReasoningMetadata.ProtectedDataKey], Is.EqualTo("second"));
            Assert.That(assembler.GetAnswerText(), Is.EqualTo("beforeafter"));
        }

        assembler.Reset();
        Assert.That(assembler.Blocks, Is.Empty);
        Assert.That(assembler.GetAnswerText(), Is.Empty);
    }

    [Test]
    public void AppendUpdate_WhenDistinctReasoningItemsAndToolCallsArrive_PreservesItemBoundariesAndClientCalls()
    {
        var assembler = new ResponseAssembler("openai-responses");

        assembler.AppendUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextReasoningContent("first ")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoningItemId"] = "rs_1" }
                }
            ]
        });
        assembler.AppendUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextReasoningContent("summary")
                {
                    ProtectedData = "enc_1",
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoningItemId"] = "rs_1" }
                }
            ]
        });
        assembler.AppendUpdate(new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new TextReasoningContent("second")
                {
                    ProtectedData = "enc_2",
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoningItemId"] = "rs_2" }
                },
                new FunctionCallContent("info-1", "hosted_search") { InformationalOnly = true },
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["path"] = "README.md" })
            ]
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assembler.Blocks, Has.Count.EqualTo(2));
            Assert.That(assembler.Blocks[0].Text?.ToString(), Is.EqualTo("first summary"));
            Assert.That(assembler.Blocks[0].Metadata[ReasoningMetadata.ItemIdKey], Is.EqualTo("rs_1"));
            Assert.That(assembler.Blocks[0].Metadata[ReasoningMetadata.ProtectedDataKey], Is.EqualTo("enc_1"));
            Assert.That(assembler.Blocks[1].Text?.ToString(), Is.EqualTo("second"));
            Assert.That(assembler.Blocks[1].Metadata[ReasoningMetadata.ItemIdKey], Is.EqualTo("rs_2"));
            Assert.That(assembler.Blocks[1].Metadata[ReasoningMetadata.ProtectedDataKey], Is.EqualTo("enc_2"));
            Assert.That(assembler.FunctionCalls, Has.Count.EqualTo(1));
            Assert.That(assembler.FunctionCalls[0].CallId, Is.EqualTo("call-1"));
        }
    }
}
