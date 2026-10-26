using Everywhere.Linux.Platform;

namespace Everywhere.Linux.Tests;

public sealed class SelectionModeWheelTests
{
    [Test]
    public void TouchpadGesture_Update_AccumulatesSmallDeltasAndLimitsRepeatRate()
    {
        var wheel = new SelectionModeWheel();
        Assert.That(wheel.Update(0.25, 1000), Is.Zero);
        Assert.That(wheel.Update(0.25, 1010), Is.Zero);
        Assert.That(wheel.Update(0.5, 1020), Is.EqualTo(-1));
        Assert.That(wheel.Update(4, 1040), Is.Zero);
        Assert.That(wheel.Update(1, 1220), Is.EqualTo(-1));
    }

    [Test]
    public void HorizontalOrZeroMotion_Update_DoesNotChangeMode()
    {
        Assert.That(new SelectionModeWheel().Update(0, 1000), Is.Zero);
    }

    [Test]
    public void DirectionReversal_Update_DoesNotReuseThePreviousPartialNotch()
    {
        var wheel = new SelectionModeWheel();
        Assert.That(wheel.Update(0.75, 1000), Is.Zero);
        Assert.That(wheel.Update(-0.75, 1050), Is.Zero);
        Assert.That(wheel.Update(-0.25, 1100), Is.EqualTo(1));
    }

    [Test]
    public void SeparateGestures_Update_DiscardsAnOldPartialNotch()
    {
        var wheel = new SelectionModeWheel();
        Assert.That(wheel.Update(0.75, 1000), Is.Zero);
        Assert.That(wheel.Update(0.5, 2000), Is.Zero);
    }
}