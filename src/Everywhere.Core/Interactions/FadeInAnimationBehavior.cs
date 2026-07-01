using System.Numerics;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Xaml.Interactivity;

namespace Everywhere.Interactions;

public class FadeInAnimationBehavior : Behavior<Visual>
{
    public TimeSpan FadeInDuration { get; set; } = TimeSpan.FromMilliseconds(600);

    public TimeSpan ItemDelayMultiplier { get; set; } = TimeSpan.FromMilliseconds(400);

    protected override void OnAttachedToVisualTree()
    {
        StartAnimation();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == Visual.IsVisibleProperty && change.NewValue is true)
        {
            StartAnimation();
        }
    }

    private void StartAnimation()
    {
        if (AssociatedObject is not { } associatedObject) return;

        var visual = ElementComposition.GetElementVisual(associatedObject);
        if (visual == null) return;

        var compositor = visual.Compositor;
        var animationGroup = compositor.CreateAnimationGroup();

        var delayTime = TimeSpan.FromMilliseconds(0);
        if (associatedObject is ListBoxItem { Parent: ListBox listBox } listBoxItem)
        {
            var index = listBox.IndexFromContainer(listBoxItem);
            delayTime = index * ItemDelayMultiplier;
        }

        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.Target = "Offset";
        offsetAnimation.DelayTime = delayTime;
        offsetAnimation.InsertKeyFrame(0, new Vector3(0, 10, 0));
        offsetAnimation.InsertKeyFrame(1, new Vector3(0, 0, 0));
        offsetAnimation.Duration = FadeInDuration;
        animationGroup.Add(offsetAnimation);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Target = "Opacity";
        opacityAnimation.DelayTime = delayTime;
        opacityAnimation.InsertKeyFrame(0, 0);
        opacityAnimation.InsertKeyFrame(1, 1);
        opacityAnimation.Duration = FadeInDuration;
        animationGroup.Add(opacityAnimation);

        visual.StartAnimationGroup(animationGroup);
    }
}