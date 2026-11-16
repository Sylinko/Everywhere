using Everywhere.Chat;
using MessagePack;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatUsageDetailsTests
{
    [Test]
    public void Accumulate_WhenOnlySomeCallsHaveMeasurableOutput_PreservesUsageAndWeightsTheirSpeed()
    {
        // Historical usage has total generation time but no first-to-last output measurement.
        var usage = new ChatUsageDetails { OutputTokenCount = 73, TotalGenerationSeconds = 0.00563 };
        AddCall(12, 99);
        Assert.That(usage.TokensPerSecond, Is.Null);

        AddCall(8, 100);
        AddCall(12, 400);

        var restored = MessagePackSerializer.Deserialize<ChatUsageDetails>(MessagePackSerializer.Serialize(usage));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.OutputTokenCount, Is.EqualTo(105));
            Assert.That(restored.TokensPerSecond, Is.EqualTo(40)); // (8 + 12) / (0.1 + 0.4)
        }

        void AddCall(long outputTokens, int intervalMilliseconds)
        {
            var call = new ChatUsageDetails { OutputTokenCount = outputTokens };
            call.RecordGenerationTiming(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(intervalMilliseconds));
            usage.Accumulate(call);
        }
    }
}