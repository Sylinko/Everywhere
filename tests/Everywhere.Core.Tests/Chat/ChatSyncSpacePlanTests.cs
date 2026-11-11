using Everywhere.Cloud;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatSyncSpacePlanTests
{
    [Test]
    public void FromInventory_WhenSpaceHasBeenFreed_TargetsOnlyUploadsStillBlocked()
    {
        // Both uploads were rejected at 100/100 bytes. Another device then freed 20 bytes:
        // the newer 10-byte upload now fits, but the older 50-byte upload still needs space.
        var now = DateTimeOffset.UtcNow;
        var older = Blocked(50, now.AddDays(-1));
        var newer = Blocked(10, now);
        var id = Guid.NewGuid();
        var releasable = new ChatSyncItem(
            id, "old history", true, new ChatSyncCopy(id.ToString("D"), "old history", 40, 1, 1),
            null, ChatSyncParticipation.Cloud, ChatSyncProgress.Synced, null)
        {
            UpdatedAt = now.AddDays(-2),
            CanReleaseSpace = true
        };
        var inventory = new ChatSyncInventory("account", 1, new ChatSyncStorageUsage(80, 100), [newer, older, releasable]);

        var plan = ChatSyncSpacePlan.FromInventory(inventory);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Target, Is.EqualTo(older.Id));
            Assert.That(plan.RequiredBytes, Is.EqualTo(30));
            Assert.That(plan.Copies, Is.EqualTo(new[] { releasable }));
        });
    }

    private static ChatSyncItem Blocked(long deltaBytes, DateTimeOffset updatedAt)
    {
        var failure = new ChatSyncFailure(ChatSyncFailureCode.QuotaExceeded, new()
        {
            ["usedBytes"] = 100L, ["limitBytes"] = 100L, ["deltaBytes"] = deltaBytes
        });
        return new ChatSyncItem(Guid.NewGuid(), "pending upload", true, null, 0,
            ChatSyncParticipation.Cloud, ChatSyncProgress.Failed, failure) { UpdatedAt = updatedAt };
    }
}