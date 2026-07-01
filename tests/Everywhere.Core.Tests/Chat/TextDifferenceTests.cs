using Everywhere.Chat.Plugins;

namespace Everywhere.Core.Tests.Chat;

public class TextDifferenceTests
{
    [Test]
    public async Task WaitForAcceptanceAsync_EmptyDiff_CompletesAsRejected()
    {
        var difference = new TextDifference("file.txt");

        var result = await difference.WaitForAcceptanceAsync();

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task WaitForAcceptanceAsync_AlreadyAcceptedDiff_ReturnsAccepted()
    {
        var difference = new TextDifference("file.txt");
        difference.Add(TextChange.Replace(0, 3, "updated"));
        difference.AcceptAll();

        var result = await difference.WaitForAcceptanceAsync();

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task WaitForAcceptanceAsync_AlreadyRejectedDiff_ReturnsRejected()
    {
        var difference = new TextDifference("file.txt");
        difference.Add(TextChange.Replace(0, 3, "updated"));
        difference.DiscardAll();

        var result = await difference.WaitForAcceptanceAsync();

        Assert.That(result, Is.False);
    }
}
