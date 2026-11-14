using System.Reflection;
using System.Text.Json;
using Everywhere.Linux.Automation;

namespace Everywhere.Linux.Tests;

public sealed class PrimarySelectionMonitorTests
{
    [Test]
    public async Task UnicodeSelection_Read_PreservesCompleteText()
    {
        const string text = "Selected 中文 🌏 e\u0301\nsecond line";
        var result = await ReadAsync(text);
        Assert.That(result, Is.EqualTo((text, false)));
    }

    [Test]
    public async Task OversizedSelection_Read_DoesNotSplitSurrogatePair()
    {
        var prefix = new string('a', PrimarySelectionMonitor.MaximumCharacters - 1);
        var result = await ReadAsync(prefix + "🌏 trailing text");
        Assert.That(result, Is.EqualTo((prefix, true)));
    }

    [Test]
    public void PointerAndWindow_RoundTrip_RetainDesktopEvidence()
    {
        var change = new PrimarySelectionChange("Synthetic text", false, "0x1234", 42, -1200, 450);
        Assert.That(JsonSerializer.Deserialize<PrimarySelectionChange>(JsonSerializer.Serialize(change)), Is.EqualTo(change));
    }

    private static Task<(string Text, bool IsIncomplete)> ReadAsync(string text)
    {
        var method = typeof(PrimarySelectionMonitor).GetMethod("ReadTextAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(PrimarySelectionMonitor));
        return method.Invoke(null, [new StringReader(text), CancellationToken.None]) as Task<(string, bool)>
            ?? throw new InvalidOperationException("The bounded reader did not return its operation.");
    }
}