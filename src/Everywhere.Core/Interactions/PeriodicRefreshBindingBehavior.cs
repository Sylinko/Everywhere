using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using ZLinq;

namespace Everywhere.Interactions;

/// <summary>
/// Periodically refreshes an existing binding without replacing it or constraining how its target
/// content is composed. All scheduler state is owned and accessed by the UI thread.
/// </summary>
public abstract class PeriodicRefreshBindingBehaviorBase : Behavior
{
    private static readonly List<PeriodicRefreshBindingBehaviorBase> EnabledBehaviors = [];
    private static readonly DispatcherTimer Timer = new(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, Tick);

    static PeriodicRefreshBindingBehaviorBase()
    {
        IsEnabledProperty.Changed.Subscribe(change =>
        {
            if (change.Sender is not PeriodicRefreshBindingBehaviorBase behavior) return;

            if (change.NewValue.Value) Register(behavior);
            else Unregister(behavior, refreshFinalValue: true);
        });
    }

    protected override void OnAttached()
    {
        if (IsEnabled) Register(this);
    }

    protected override void OnDetaching()
    {
        Unregister(this, refreshFinalValue: false);
    }

    private static void Register(PeriodicRefreshBindingBehaviorBase behavior)
    {
        if (behavior.AssociatedObject is null) return;

        if (!EnabledBehaviors.Contains(behavior)) EnabledBehaviors.Add(behavior);
        UpdateTimerState();
    }

    private static void Unregister(PeriodicRefreshBindingBehaviorBase behavior, bool refreshFinalValue)
    {
        EnabledBehaviors.Remove(behavior);
        if (refreshFinalValue && behavior.AssociatedObject is not null) behavior.Refresh();
        UpdateTimerState();
    }

    private static void Tick(object? sender, EventArgs e)
    {
        // DispatcherTimer and lifecycle callbacks run synchronously on the UI thread. Refresh only
        // updates Text/Run bindings, so registration cannot be interleaved with this loop.
        for (var i = EnabledBehaviors.Count - 1; i >= 0; i--)
        {
            var behavior = EnabledBehaviors[i];
            if (behavior.AssociatedObject is not null && behavior.IsEnabled)
            {
                behavior.Refresh();
                continue;
            }

            EnabledBehaviors.RemoveAt(i);
        }

        UpdateTimerState();
    }

    private static void UpdateTimerState()
    {
        if (EnabledBehaviors.Count == 0)
        {
            if (Timer.IsEnabled) Timer.Stop();
        }
        else if (!Timer.IsEnabled)
        {
            Timer.Start();
        }
    }

    protected abstract void Refresh();
}

/// <summary>
/// Refreshes a <see cref="TextBlock.Text"/> binding, a <see cref="Run.Text"/> binding, or the direct Run bindings contained by a TextBlock.
/// </summary>
public sealed class PeriodicRefreshTextBehavior : PeriodicRefreshBindingBehaviorBase
{
    protected override void Refresh()
    {
        switch (AssociatedObject)
        {
            case Run run:
                RealUpdateTarget(BindingOperations.GetBindingExpressionBase(run, Run.TextProperty));
                break;
            case TextBlock { Inlines: { Count: > 0 } inlines }:
                foreach (var run in inlines.AsValueEnumerable().OfType<Run>())
                    RealUpdateTarget(BindingOperations.GetBindingExpressionBase(run, Run.TextProperty));
                break;
            case TextBlock textBlock:
                RealUpdateTarget(BindingOperations.GetBindingExpressionBase(textBlock, TextBlock.TextProperty));
                break;
        }
    }

    /// <summary>
    /// Workaround fix: `MultiBindingExpression` does not override the `UpdateTarget` method
    /// </summary>
    /// <param name="expression"></param>
    private static void RealUpdateTarget(BindingExpressionBase? expression)
    {
        if (expression is null) return;

        if (expression.GetType().Name == "MultiBindingExpression")
        {
            MultiBindingExpression_PublishValue(expression);
        }
        else
        {
            expression.UpdateTarget();
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "PublishValue")]
    private static extern void MultiBindingExpression_PublishValue(
        [UnsafeAccessorType("Avalonia.Data.Core.MultiBindingExpression, Avalonia.Base")] object multiBindingExpression);
}