using Avalonia.Input;
using Everywhere.Extensions;

namespace Everywhere.Core.Tests.Extensions;

public sealed class AvaloniaExtensionsTests
{
    private static bool IsMacOS => OperatingSystem.IsMacOS();

    [Test]
    public void ApplicationShortcutModifier_Control_AlwaysMatches()
    {
        Assert.Multiple(() =>
        {
            Assert.That(KeyModifiers.Control.HasApplicationShortcutModifier(), Is.True);
            Assert.That(KeyModifiers.Control.IsApplicationShortcutModifierOnly(), Is.True);
        });
    }

    [Test]
    public void ApplicationShortcutModifier_WithoutControlOrCommand_NeverMatches()
    {
        var modifiers = new[]
        {
            KeyModifiers.None,
            KeyModifiers.Shift,
            KeyModifiers.Alt,
            KeyModifiers.Shift | KeyModifiers.Alt
        };

        foreach (var value in modifiers)
        {
            Assert.Multiple(() =>
            {
                Assert.That(value.HasApplicationShortcutModifier(), Is.False, $"Has matched for {value}");
                Assert.That(value.IsApplicationShortcutModifierOnly(), Is.False, $"Only matched for {value}");
            });
        }
    }

    [Test]
    public void ApplicationShortcutModifier_ControlWithExtraModifiers_OnlyHasMatches()
    {
        var modifiers = new[]
        {
            KeyModifiers.Control | KeyModifiers.Shift,
            KeyModifiers.Control | KeyModifiers.Alt,
            KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt
        };

        foreach (var value in modifiers)
        {
            Assert.Multiple(() =>
            {
                Assert.That(value.HasApplicationShortcutModifier(), Is.True, $"Has did not match for {value}");
                Assert.That(value.IsApplicationShortcutModifierOnly(), Is.False, $"Only matched for {value}");
            });
        }
    }

    [Test]
    public void ApplicationShortcutModifier_Command_MatchesOnMacOSOnly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(KeyModifiers.Meta.HasApplicationShortcutModifier(), Is.EqualTo(IsMacOS));
            Assert.That(KeyModifiers.Meta.IsApplicationShortcutModifierOnly(), Is.EqualTo(IsMacOS));
            Assert.That((KeyModifiers.Meta | KeyModifiers.Shift).HasApplicationShortcutModifier(), Is.EqualTo(IsMacOS));
            Assert.That((KeyModifiers.Meta | KeyModifiers.Shift).IsApplicationShortcutModifierOnly(), Is.False);
        });
    }

    [Test]
    public void ApplicationShortcutModifier_CommandWithControl_MatchesOnEveryPlatform()
    {
        const KeyModifiers modifiers = KeyModifiers.Control | KeyModifiers.Meta;

        Assert.Multiple(() =>
        {
            Assert.That(modifiers.HasApplicationShortcutModifier(), Is.True);
            Assert.That(modifiers.IsApplicationShortcutModifierOnly(), Is.False);
        });
    }
}
