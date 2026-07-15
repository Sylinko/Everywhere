using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Transformation;

namespace Everywhere.Views;

/// <summary>
/// Hosts one stable flat presentation row and applies presentation-only insertion animation.
/// </summary>
public sealed class ChatPresentationRowPresenter : ContentControl
{
    private ContentPresenter? _rowContentPresenter;
    private bool _animateInsertionPending;

    /// <summary>
    /// Assigns a projected row to this recyclable container. Only rows not previously presented by
    /// the owning surface receive the inexpensive transform/opacity insertion animation.
    /// </summary>
    public void SetRow(ChatPresentationRow row, bool animateInsertion)
    {
        Content = row;
        _animateInsertionPending = animateInsertion;
        ApplyInsertionAnimationState();
    }

    public void ClearRow()
    {
        _animateInsertionPending = false;
        Content = null;
        ResetPresenter();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _rowContentPresenter = e.NameScope.Find<ContentPresenter>("PART_RowContentPresenter");
        ApplyInsertionAnimationState();
    }

    private void ApplyInsertionAnimationState()
    {
        if (_rowContentPresenter is not { } presenter) return;
        if (!_animateInsertionPending)
        {
            ResetPresenter();
            return;
        }

        _animateInsertionPending = false;
        var transitions = presenter.Transitions;
        presenter.Transitions = null;
        presenter.Opacity = 0;
        presenter.RenderTransform = TransformOperations.Parse("translateY(5px)");
        presenter.Transitions = transitions;
        presenter.Opacity = 1;
        presenter.RenderTransform = TransformOperations.Identity;
    }

    private void ResetPresenter()
    {
        if (_rowContentPresenter is not { } presenter) return;
        var transitions = presenter.Transitions;
        presenter.Transitions = null;
        presenter.Opacity = 1;
        presenter.RenderTransform = TransformOperations.Identity;
        presenter.Transitions = transitions;
    }
}