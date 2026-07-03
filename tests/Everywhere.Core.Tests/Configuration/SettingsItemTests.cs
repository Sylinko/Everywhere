using Everywhere.Configuration;

namespace Everywhere.Core.Tests.Configuration;

public sealed class SettingsItemTests
{
    [Test]
    public void IsExpandable_DoesNotOverrideExplicitCollapsedState()
    {
        var item = new EmptySettingsTemplatedItem
        {
            IsExpanded = false
        };

        item.IsExpandable = true;

        Assert.That(item.IsExpanded, Is.False);
    }

    [Test]
    public void IsExpandable_WhenDisabledCollapsesItem()
    {
        var item = new EmptySettingsTemplatedItem
        {
            IsExpanded = true
        };

        item.IsExpandable = true;
        item.IsExpandable = false;

        Assert.That(item.IsExpanded, Is.False);
    }
}
