using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Everywhere.Views;

public sealed class QuickModeVisualElementCardCarousel : ItemsControl
{
    private sealed class ItemsPanelImpl : Canvas
    {
        private bool _isExpanded;
        private double _scrollOffset;

        private const double CardSpacing = 20.0;
        private const double ScrollSpeed = 50.0;

        public ItemsPanelImpl()
        {
            Background = Brushes.Transparent;
            ClipToBounds = false;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateLayoutState();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var height = 0d;
            var infSize = new Size(double.PositiveInfinity, double.PositiveInfinity);
            foreach (var child in Children)
            {
                child.Measure(infSize);
                height = Math.Max(height, child.DesiredSize.Height);
            }

            return new Size(0d, height);
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);

            if (Children.Count > 1 && !_isExpanded)
            {
                _isExpanded = true;
                UpdateLayoutState();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);

            if (_isExpanded)
            {
                _isExpanded = false;
                UpdateLayoutState();
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            if (_isExpanded && Children.Count > 1)
            {
                var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
                _scrollOffset -= delta * ScrollSpeed;
                ClampScrollOffset();
                UpdateLayoutState();
                e.Handled = true;
            }
        }

        protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            base.ChildrenChanged(sender, e);
            
            if (e.NewItems != null)
            {
                foreach (var child in e.NewItems.OfType<QuickModeVisualElementCardContainer>())
                {
                    if (double.IsNaN(GetLeft(child)))
                    {
                        SetLeft(child, Bounds.Width / 2.0);
                        SetTop(child, Bounds.Height / 2.0);
                    }

                    child.SizeChanged += HandleChildSizeChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (var child in e.OldItems.OfType<QuickModeVisualElementCardContainer>())
                {
                    child.SizeChanged -= HandleChildSizeChanged;
                }
            }

            Dispatcher.UIThread.Post(UpdateLayoutState, DispatcherPriority.BeforeRender);
        }

        private void HandleChildSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            UpdateLayoutState();
        }

        private void UpdateLayoutState()
        {
            if (Children.Count == 0) return;

            if (Children.Count == 1)
            {
                if (Children[0] is QuickModeVisualElementCardContainer child)
                {
                    var left = (Bounds.Width - child.Bounds.Width) / 2.0;
                    var top = (Bounds.Height - child.Bounds.Height) / 2.0;
                    SetChildPositionAndRotation(child, left, top, 0);
                }

                return;
            }

            if (_isExpanded)
            {
                ClampScrollOffset();
                var totalWidth = GetTotalExpandedWidth();
                var startX = (Bounds.Width > totalWidth) ? (Bounds.Width - totalWidth) / 2.0 : 0;
                var currentX = startX - _scrollOffset;

                foreach (var child in Children.OfType<QuickModeVisualElementCardContainer>())
                {
                    var left = currentX;
                    var top = (Bounds.Height - child.Bounds.Height) / 2.0;
                    SetChildPositionAndRotation(child, left, top, 0);
                    currentX += child.Bounds.Width + CardSpacing;
                }
            }
            else
            {
                var i = 0;
                foreach (var child in Children.OfType<QuickModeVisualElementCardContainer>())
                {
                    var left = (Bounds.Width - child.Bounds.Width) / 2.0;
                    var top = (Bounds.Height - child.Bounds.Height) / 2.0;
                    SetChildPositionAndRotation(child, left, top, Random.Shared.Next(3, 10) * (i++ % 2 == 0 ? 1 : -1));
                }
            }
        }

        private static void SetChildPositionAndRotation(QuickModeVisualElementCardContainer child, double left, double top, double angle)
        {
            SetLeft(child, left);
            SetTop(child, top);
            child.RotateAngle = angle;
        }

        private double GetTotalExpandedWidth()
        {
            var total = Children.OfType<QuickModeVisualElementCardContainer>().Sum(child => child.Bounds.Width);
            if (Children.Count > 1)
            {
                total += (Children.Count - 1) * CardSpacing;
            }
            return total;
        }

        private void ClampScrollOffset()
        {
            var totalWidth = GetTotalExpandedWidth();
            var maxScroll = Math.Max(0, totalWidth - Bounds.Width);
            _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
        }
    }

    public QuickModeVisualElementCardCarousel()
    {
        ClipToBounds = false;
        ItemsPanel = new FuncTemplate<Panel?>(() => new ItemsPanelImpl());
    }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        return NeedsContainer<QuickModeVisualElementCardContainer>(item, out recycleKey);
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return new QuickModeVisualElementCardContainer
        {
            DataContext = item
        };
    }
}