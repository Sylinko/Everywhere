using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Cloud;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Messages;
using Everywhere.Statistics;
using Everywhere.Utilities;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.ViewModels;

/// <summary>
/// Provides the home dashboard with local usage statistics, quick configuration entries, and navigation commands.
/// </summary>
public sealed partial class HomePageViewModel : ReactiveViewModelBase
{
    private const int HeatmapMonths = 6;

    [ObservableProperty] public partial string? TodayText { get; set; }

    [ObservableProperty] public partial StatisticsOverview? Overview { get; private set; }

    [ObservableProperty] public partial StatisticsOverview? MonthlyOverview { get; private set; }

    [ObservableProperty] public partial IDynamicResourceKey TurnAverageKey { get; private set; } =
        new FormattedDynamicResourceKey(LocaleKey.HomePage_TurnAverage, new DirectResourceKey("0"));

    [ObservableProperty] public partial IDynamicResourceKey HeatmapSummaryKey { get; private set; } =
        new FormattedDynamicResourceKey(
            LocaleKey.HomePage_HeatmapSummary,
            new FormattedDynamicResourceKey(
                LocaleKey.HomePage_HeatmapValueLabel,
                new DirectResourceKey("0"),
                new DynamicResourceKey(LocaleKey.HomePage_Topics)),
            new DirectResourceKey("0"));

    [ObservableProperty] public partial IDynamicResourceKey HeatmapRangeKey { get; private set; } =
        new FormattedDynamicResourceKey(LocaleKey.HomePage_LastMonths, new DirectResourceKey(HeatmapMonths.ToString(CultureInfo.CurrentCulture)));

    [ObservableProperty] public partial HeatmapMetricTabItem? SelectedHeatmapMetricItem { get; set; }

    [ObservableProperty] public partial IReadOnlyList<IStatisticsHeatmapDay> HeatmapDays { get; set; } = [];

    public ICloudClient CloudClient { get; }

    public IReadOnlyBindableList<DynamicNotification> Notifications { get; }

    public ObservableCollection<HeatmapMetricTabItem> HeatmapMetrics { get; } = [];

    public ObservableCollection<QuickConfigurationCardItem> QuickConfigurationCards { get; } = [];

    private readonly IStatisticsService _statisticsService;
    private readonly IServiceProvider _serviceProvider;

    public HomePageViewModel(
        ICloudClient cloudClient,
        INotificationCenter notificationCenter,
        IStatisticsService statisticsService,
        IServiceProvider serviceProvider)
    {
        CloudClient = cloudClient;
        Notifications = notificationCenter.Notifications;
        _statisticsService = statisticsService;
        _serviceProvider = serviceProvider;

        InitializeHeatmapMetrics();
    }

    /// <inheritdoc />
    protected internal override async Task ViewLoaded(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await base.ViewLoaded(cancellationToken);
    }

    partial void OnSelectedHeatmapMetricItemChanged(HeatmapMetricTabItem? value)
    {
        // Use command for async execution and forbid concurrent execution of the refresh operation.
        RefreshHeatmapCommand.ExecuteAsync(value).Detach(ToastExceptionHandler);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        TodayText = now.ToString("D", CultureInfo.CurrentCulture);
        HeatmapRangeKey = new FormattedDynamicResourceKey(
            LocaleKey.HomePage_LastMonths,
            new DirectResourceKey(HeatmapMonths.ToString(CultureInfo.CurrentCulture)));

        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);
        var nextMonthStart = monthStart.AddMonths(1);
        var monthlyOverview = await _statisticsService.GetOverviewAsync(
            new StatisticsRange(monthStart, nextMonthStart),
            StatisticsDeviceScope.AllDevices,
            cancellationToken);
        var overview = await _statisticsService.GetOverviewAsync(
            new StatisticsRange(DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            StatisticsDeviceScope.AllDevices,
            cancellationToken);

        MonthlyOverview = monthlyOverview;
        ApplyOverview(overview);
        ApplyQuickConfiguration(_statisticsService.GetCapabilitySummary());
        await RefreshHeatmapAsync(SelectedHeatmapMetricItem, cancellationToken);
    }

    [RelayCommand]
    private async Task RefreshHeatmapAsync(HeatmapMetricTabItem? metricItem, CancellationToken cancellationToken)
    {
        if (metricItem is null) return;

        var days = await _statisticsService.GetHeatmapAsync(
            metricItem.Metric,
            HeatmapMonths,
            StatisticsDeviceScope.AllDevices,
            cancellationToken);
        HeatmapDays = days;
        HeatmapSummaryKey = CreateHeatmapSummaryKey(metricItem.Metric, days);
    }

    [RelayCommand]
    private static void StartChat()
    {
        WeakReferenceMessenger.Default.Send(new ActivateChatSessionMessage());
    }

    [RelayCommand]
    private static void NavigateTo(string route)
    {
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(route));
    }

    [RelayCommand]
    private void OpenWelcomeDialog()
    {
        DialogManager
            .CreateCustomDialog(_serviceProvider.GetRequiredService<WelcomeView>())
            .ShowAsync();
    }

    [RelayCommand]
    private void OpenChangeLog()
    {
        _serviceProvider.GetRequiredService<MainViewModel>()
            .NavigateTo(_serviceProvider.GetRequiredService<ChangeLogView>());
    }

    private void InitializeHeatmapMetrics()
    {
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicResourceKey(LocaleKey.HomePage_Topics), StatisticsHeatmapMetric.Topics));
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicResourceKey(LocaleKey.HomePage_Turns), StatisticsHeatmapMetric.Turns));
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicResourceKey(LocaleKey.HomePage_Tokens), StatisticsHeatmapMetric.Tokens));
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicResourceKey(LocaleKey.HomePage_VisualContext), StatisticsHeatmapMetric.VisualContext));
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicResourceKey(LocaleKey.HomePage_ToolUsage), StatisticsHeatmapMetric.ToolUsage));
        SelectedHeatmapMetricItem = HeatmapMetrics[0];
    }

    private void ApplyOverview(StatisticsOverview overview)
    {
        Overview = overview;
        TurnAverageKey = new FormattedDynamicResourceKey(
            LocaleKey.HomePage_TurnAverage,
            new DirectResourceKey(overview.TopicCount > 0
                ? ((double)overview.TurnCount / overview.TopicCount).ToString("0.#", CultureInfo.CurrentCulture)
                : "0"));
    }

    private void ApplyQuickConfiguration(StatisticsCapabilitySummary summary)
    {
        QuickConfigurationCards.Reset(
            new QuickConfigurationCardItem(
                new DynamicResourceKey(LocaleKey.HomePage_Assistants),
                summary.Assistants.TotalCount.ToString("N0", CultureInfo.CurrentCulture),
                LucideIconKind.Bot,
                "CustomAssistantPage"),
            new QuickConfigurationCardItem(
                new DynamicResourceKey(LocaleKey.HomePage_BuiltInTools),
                FormatCapabilityCount(summary.BuiltInTools),
                LucideIconKind.Hammer,
                "ChatPluginPage"),
            new QuickConfigurationCardItem(
                new DynamicResourceKey(LocaleKey.HomePage_Mcp),
                FormatCapabilityCount(summary.Mcp),
                LucideIconKind.Unplug,
                "ChatPluginPage"),
            new QuickConfigurationCardItem(
                new DynamicResourceKey(LocaleKey.HomePage_Skills),
                FormatCapabilityCount(summary.Skills),
                LucideIconKind.Sparkles,
                "SkillPage"));
    }

    private static FormattedDynamicResourceKey CreateHeatmapSummaryKey(StatisticsHeatmapMetric metric, IReadOnlyList<IStatisticsHeatmapDay> days)
    {
        var total = days.Sum(x => x.Value);
        var streak = GetLongestStreak(days);
        var valueLabelKey = new FormattedDynamicResourceKey(
            LocaleKey.HomePage_HeatmapValueLabel,
            new DirectResourceKey(Humanizer.HumanizeNumber(total)),
            GetMetricLabelKey(metric));
        return new FormattedDynamicResourceKey(
            LocaleKey.HomePage_HeatmapSummary,
            valueLabelKey,
            new DirectResourceKey(streak.ToString("N0", CultureInfo.CurrentCulture)));
    }

    private static DynamicResourceKey GetMetricLabelKey(StatisticsHeatmapMetric metric) => metric switch
    {
        StatisticsHeatmapMetric.Topics => new DynamicResourceKey(LocaleKey.HomePage_Topics),
        StatisticsHeatmapMetric.Turns => new DynamicResourceKey(LocaleKey.HomePage_Turns),
        StatisticsHeatmapMetric.Tokens => new DynamicResourceKey(LocaleKey.HomePage_Tokens),
        StatisticsHeatmapMetric.VisualContext => new DynamicResourceKey(LocaleKey.HomePage_VisualContext),
        StatisticsHeatmapMetric.ToolUsage => new DynamicResourceKey(LocaleKey.HomePage_ToolUsage),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null)
    };

    private static int GetLongestStreak(IReadOnlyList<IStatisticsHeatmapDay> days)
    {
        var activeDays = days.Where(x => x.Value > 0).Select(x => x.Date).ToHashSet();
        if (activeDays.Count == 0) return 0;

        var longest = 0;
        var current = 0;
        var start = activeDays.Min();
        var end = activeDays.Max();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (activeDays.Contains(date))
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }

    private static string FormatCapabilityCount(StatisticsCapabilityGroup group) =>
        $"{group.EnabledCount.ToString("N0", CultureInfo.CurrentCulture)}/{group.TotalCount.ToString("N0", CultureInfo.CurrentCulture)}";

    /// <summary>
    /// View model for a heatmap metric tab item, containing the display name and associated metric type.
    /// </summary>
    /// <param name="Name"></param>
    /// <param name="Metric"></param>
    public sealed record HeatmapMetricTabItem(IDynamicResourceKey Name, StatisticsHeatmapMetric Metric);

    /// <summary>
    /// View model for a quick configuration card item, containing the display name, count text, icon, and navigation route.
    /// </summary>
    /// <param name="Name"></param>
    /// <param name="CountText"></param>
    /// <param name="Icon"></param>
    /// <param name="Route"></param>
    public sealed record QuickConfigurationCardItem(
        IDynamicResourceKey Name,
        string CountText,
        LucideIconKind Icon,
        string Route
    );
}
