using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Statistics;
using ZLinq;

namespace Everywhere.Views;

/// <summary>
/// Renders the home dashboard activity heatmap without introducing a charting dependency.
/// </summary>
public sealed class StatisticsHeatmap : UserControl, IRecipient<LocaleChangedMessage>
{
    public static readonly StyledProperty<IReadOnlyList<IStatisticsHeatmapDay>?> DaysProperty =
        AvaloniaProperty.Register<StatisticsHeatmap, IReadOnlyList<IStatisticsHeatmapDay>?>(nameof(Days));

    public static readonly StyledProperty<int> MonthsProperty =
        AvaloniaProperty.Register<StatisticsHeatmap, int>(nameof(Months), 6);

    private const double CellSize = 20;
    private const double CellGap = 4;
    private const double LeftLabelWidth = 34;
    private const double TopLabelHeight = 24;
    private const double CellPitch = CellSize + CellGap;

    private readonly Dictionary<Rect, IStatisticsHeatmapDay> _hitTargets = [];
    private readonly Border _toolTipHolder;
    private readonly DynamicResourceKey _mondayKey = new(LocaleKey.HomePage_HeatmapWeekdayMondayShort);
    private readonly DynamicResourceKey _wednesdayKey = new(LocaleKey.HomePage_HeatmapWeekdayWednesdayShort);
    private readonly DynamicResourceKey _fridayKey = new(LocaleKey.HomePage_HeatmapWeekdayFridayShort);

    public IReadOnlyList<IStatisticsHeatmapDay>? Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public int Months
    {
        get => GetValue(MonthsProperty);
        set => SetValue(MonthsProperty, value);
    }

    public StatisticsHeatmap()
    {
        Content = _toolTipHolder = new Border
        {
            Background = Brushes.Transparent,
            Width = CellSize,
            Height = CellSize,
            [ToolTip.PlacementProperty] = PlacementMode.Top
        };
        _toolTipHolder.PointerMoved += (_, args) => OnPointerMoved(args);
    }

    protected override void ArrangeCore(Rect finalRect)
    {
        base.ArrangeCore(finalRect);

        _toolTipHolder.Arrange(new Rect(-CellSize, -CellSize, CellSize, CellSize));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = GetDesiredContentSize();
        var width = double.IsInfinity(availableSize.Width) ? desired.Width : Math.Min(desired.Width, availableSize.Width);
        return new Size(Math.Max(0, width), desired.Height);
    }

    private Size GetDesiredContentSize()
    {
        var range = CreateRange();
        var weeks = Math.Max(1, ((range.End.DayNumber - range.AlignedStart.DayNumber) / 7) + 1);
        return new Size(LeftLabelWidth + weeks * CellPitch, TopLabelHeight + 7 * CellPitch);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        _hitTargets.Clear();
        var days = (Days ?? []).AsValueEnumerable().ToDictionary(x => x.Date);
        var max = Math.Max(1, days.Values.AsValueEnumerable().Select(x => x.Value).DefaultIfEmpty(0).Max());
        var range = CreateRange();
        var desiredGridWidth = Math.Max(0, GetDesiredContentSize().Width - LeftLabelWidth);
        var availableGridWidth = Math.Max(0, Bounds.Width - LeftLabelWidth);
        var gridOffsetX = Math.Min(0, availableGridWidth - desiredGridWidth);

        DrawWeekdayLabels(context);

        using (context.PushClip(new Rect(LeftLabelWidth, 0, availableGridWidth, Bounds.Height)))
        {
            DrawMonthLabels(context, range, gridOffsetX);

            for (var date = range.AlignedStart; date <= range.End; date = date.AddDays(1))
            {
                if (date < range.Start || date > range.Today)
                {
                    continue;
                }

                days.TryGetValue(date, out var day);
                var value = day?.Value ?? 0;
                var rect = GetCellRect(range.AlignedStart, date).Translate(new Vector(gridOffsetX, 0));
                var brush = CreateBrush(GetLevel(value, max));
                context.DrawRectangle(brush, null, rect, 4d, 4d);

                var visibleRect = rect.Intersect(new Rect(LeftLabelWidth, 0, availableGridWidth, Bounds.Height));
                if (visibleRect.Width > 0 && visibleRect.Height > 0)
                {
                    _hitTargets[visibleRect] = day ?? new StatisticsSimpleHeatmapDay(date, 0);
                }
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);
        foreach (var (rect, day) in _hitTargets.AsValueEnumerable())
        {
            if (!rect.Contains(point)) continue;

            ToolTip.SetTip(_toolTipHolder, day.ToolTipKey.ToString());
            _toolTipHolder.Arrange(new Rect(rect.Position, new Size(CellSize, CellSize)));
            return;
        }

        ToolTip.SetTip(_toolTipHolder, null);
        _toolTipHolder.Arrange(new Rect(-CellSize, -CellSize, CellSize, CellSize));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        ToolTip.SetTip(_toolTipHolder, null);
        _toolTipHolder.Arrange(new Rect(-CellSize, -CellSize, CellSize, CellSize));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DaysProperty || change.Property == MonthsProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        WeakReferenceMessenger.Default.Register(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<LocaleChangedMessage>(this);

        base.OnDetachedFromVisualTree(e);
    }

    public void Receive(LocaleChangedMessage message)
    {
        InvalidateVisual();
    }

    private void DrawWeekdayLabels(DrawingContext context)
    {
        var labels = new[]
        {
            (1, _mondayKey.ToString() ?? string.Empty),
            (3, _wednesdayKey.ToString() ?? string.Empty),
            (5, _fridayKey.ToString() ?? string.Empty)
        };
        foreach (var (row, label) in labels.AsValueEnumerable())
        {
            var y = TopLabelHeight + row * CellPitch + 1;
            context.DrawText(CreateText(label), new Point(0, y));
        }
    }

    private void DrawMonthLabels(DrawingContext context, HeatmapRange range, double offsetX)
    {
        var lastMonth = -1;
        for (var date = range.Start; date <= range.Today; date = date.AddDays(1))
        {
            if (date.Month == lastMonth) continue;

            lastMonth = date.Month;
            var rect = GetCellRect(range.AlignedStart, date).Translate(new Vector(offsetX, 0));
            context.DrawText(CreateText(date.ToString("MMM")), new Point(rect.X, 0));
        }
    }

    private static Rect GetCellRect(DateOnly alignedStart, DateOnly date)
    {
        var days = date.DayNumber - alignedStart.DayNumber;
        var column = days / 7;
        var row = ((int)date.DayOfWeek + 6) % 7;
        return new Rect(
            LeftLabelWidth + column * CellPitch,
            TopLabelHeight + row * CellPitch,
            CellSize,
            CellSize);
    }

    private HeatmapRange CreateRange()
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.Date);
        var monthCount = Math.Clamp(Months, 1, 24);
        var start = new DateOnly(today.Year, today.Month, 1).AddMonths(1 - monthCount);
        var alignedStart = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
        return new HeatmapRange(start, alignedStart, today);
    }

    private static int GetLevel(long value, long max)
    {
        return value <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(value / (double)max * 5), 1, 5);
    }

    private static SolidColorBrush CreateBrush(int level) => level switch
    {
        0 => new SolidColorBrush(Color.FromRgb(42, 43, 49)),
        1 => new SolidColorBrush(Color.FromRgb(31, 54, 87)),
        2 => new SolidColorBrush(Color.FromRgb(38, 82, 139)),
        3 => new SolidColorBrush(Color.FromRgb(42, 112, 202)),
        4 => new SolidColorBrush(Color.FromRgb(68, 143, 246)),
        _ => new SolidColorBrush(Color.FromRgb(112, 166, 255))
    };

    private FormattedText CreateText(string text) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection, new Typeface(FontFamily.Default), FontSize, Foreground);

    private sealed record HeatmapRange(DateOnly Start, DateOnly AlignedStart, DateOnly Today)
    {
        public DateOnly End => Today;
    }
}
