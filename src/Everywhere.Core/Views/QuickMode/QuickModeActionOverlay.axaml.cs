using Avalonia.Controls;
using DynamicData;
using Everywhere.Common;
using Everywhere.StrategyEngine;

namespace Everywhere.Views;

public partial class QuickModeActionOverlay : Window
{
    private readonly SourceList<QuickModeVisualElement> _elementsSource = new();

    public QuickModeActionOverlay(IEnumerable<QuickModeVisualElement> elements, IEnumerable<Strategy> strategies)
    {
        InitializeComponent();

        _elementsSource.AddRange(elements);
        CardCarousel.ItemsSource = _elementsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _);
        StrategyItemsControl.ItemsSource = strategies;
    }
}