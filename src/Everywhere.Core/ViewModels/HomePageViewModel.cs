using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Chat.Plugins;
using Everywhere.Cloud;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Messages;
using Everywhere.Skills;
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

    [ObservableProperty] public partial IDynamicLocaleKey TurnAverageKey { get; private set; } =
        new FormattedDynamicLocaleKey(LocaleKey.HomePage_TurnAverage, new DirectLocaleKey("0"));

    [ObservableProperty] public partial IDynamicLocaleKey HeatmapSummaryKey { get; private set; } =
        new FormattedDynamicLocaleKey(
            LocaleKey.HomePage_HeatmapSummary,
            new FormattedDynamicLocaleKey(
                LocaleKey.HomePage_HeatmapValueLabel,
                new DirectLocaleKey("0"),
                new DynamicLocaleKey(LocaleKey.HomePage_Topics)),
            new DirectLocaleKey("0"));

    [ObservableProperty] public partial IDynamicLocaleKey HeatmapRangeKey { get; private set; } =
        new FormattedDynamicLocaleKey(LocaleKey.HomePage_LastMonths, new DirectLocaleKey(HeatmapMonths.ToString(CultureInfo.CurrentCulture)));

    [ObservableProperty] public partial HeatmapMetricTabItem? SelectedHeatmapMetricItem { get; set; }

    [ObservableProperty] public partial IReadOnlyList<IStatisticsHeatmapDay>? HeatmapDays { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<QuickConfigurationCardItem>? QuickConfigurationCards { get; private set; }

    public ICloudClient CloudClient { get; }

    public IReadOnlyBindableList<DynamicNotification> Notifications { get; }

    public ObservableCollection<HeatmapMetricTabItem> HeatmapMetrics { get; } = [];

    private readonly IStatisticsService _statisticsService;
    private readonly IServiceProvider _serviceProvider;
    private readonly Lock _syncLock = new();

    private QuickConfigurationProvider? _quickConfigurationProvider;

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
    protected internal override Task ViewLoaded(CancellationToken cancellationToken)
    {
        Task.Run(() => InitializeAsync(cancellationToken), cancellationToken).Detach(ToastExceptionHandler);

        return base.ViewLoaded(cancellationToken);
    }

    protected internal override Task ViewUnloaded()
    {
        lock (_syncLock)
        {
            QuickConfigurationCards = null;
            DisposeHelper.DisposeToDefault(ref _quickConfigurationProvider);
        }

        return base.ViewUnloaded();
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
        HeatmapRangeKey = new FormattedDynamicLocaleKey(
            LocaleKey.HomePage_LastMonths,
            new DirectLocaleKey(HeatmapMonths.ToString(CultureInfo.CurrentCulture)));

        var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);
        var nextMonthStart = monthStart.AddMonths(1);
        MonthlyOverview = await _statisticsService.GetOverviewAsync(
            new StatisticsRange(monthStart, nextMonthStart),
            StatisticsDeviceScope.AllDevices,
            cancellationToken);

        var overview = await _statisticsService.GetOverviewAsync(
            new StatisticsRange(DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            StatisticsDeviceScope.AllDevices,
            cancellationToken);
        Overview = overview;
        TurnAverageKey = new FormattedDynamicLocaleKey(
            LocaleKey.HomePage_TurnAverage,
            new DirectLocaleKey(
                overview.TopicCount > 0 ? ((double)overview.TurnCount / overview.TopicCount).ToString("0.#", CultureInfo.CurrentCulture) : "0"));

        await RefreshHeatmapAsync(SelectedHeatmapMetricItem, cancellationToken);

        lock (_syncLock)
        {
            if (_quickConfigurationProvider is null)
            {
                _quickConfigurationProvider = new QuickConfigurationProvider(
                    _serviceProvider.GetRequiredService<Settings>(),
                    _serviceProvider.GetRequiredService<IChatPluginManager>(),
                    _serviceProvider.GetRequiredService<ISkillManager>());
                QuickConfigurationCards = _quickConfigurationProvider.Cards;
            }
        }
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
        DialogHost
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
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicLocaleKey(LocaleKey.HomePage_Topics), StatisticsHeatmapMetric.Topics));
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicLocaleKey(LocaleKey.HomePage_Turns), StatisticsHeatmapMetric.Turns));
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicLocaleKey(LocaleKey.HomePage_Tokens), StatisticsHeatmapMetric.Tokens));
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicLocaleKey(LocaleKey.HomePage_VisualContext), StatisticsHeatmapMetric.VisualContext));
        HeatmapMetrics.Add(new HeatmapMetricTabItem(new DynamicLocaleKey(LocaleKey.HomePage_ToolUsage), StatisticsHeatmapMetric.ToolUsage));
        SelectedHeatmapMetricItem = HeatmapMetrics[0];
    }

    private static FormattedDynamicLocaleKey CreateHeatmapSummaryKey(StatisticsHeatmapMetric metric, IReadOnlyList<IStatisticsHeatmapDay> days)
    {
        var total = days.Sum(x => x.Value);
        var streak = GetLongestStreak(days);
        var valueLabelKey = new FormattedDynamicLocaleKey(
            LocaleKey.HomePage_HeatmapValueLabel,
            new DirectLocaleKey(Humanizer.HumanizeNumber(total)),
            GetMetricLabelKey(metric));
        return new FormattedDynamicLocaleKey(
            LocaleKey.HomePage_HeatmapSummary,
            valueLabelKey,
            new DirectLocaleKey(streak.ToString("N0", CultureInfo.CurrentCulture)));
    }

    private static DynamicLocaleKey GetMetricLabelKey(StatisticsHeatmapMetric metric) => metric switch
    {
        StatisticsHeatmapMetric.Topics => new DynamicLocaleKey(LocaleKey.HomePage_Topics),
        StatisticsHeatmapMetric.Turns => new DynamicLocaleKey(LocaleKey.HomePage_Turns),
        StatisticsHeatmapMetric.Tokens => new DynamicLocaleKey(LocaleKey.HomePage_Tokens),
        StatisticsHeatmapMetric.VisualContext => new DynamicLocaleKey(LocaleKey.HomePage_VisualContext),
        StatisticsHeatmapMetric.ToolUsage => new DynamicLocaleKey(LocaleKey.HomePage_ToolUsage),
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

    /// <summary>
    /// View model for a heatmap metric tab item, containing the display name and associated metric type.
    /// </summary>
    /// <param name="Name"></param>
    /// <param name="Metric"></param>
    public sealed record HeatmapMetricTabItem(IDynamicLocaleKey Name, StatisticsHeatmapMetric Metric);

    /// <summary>
    /// Bindable quick configuration card shown on the home dashboard.
    /// </summary>
    public sealed partial class QuickConfigurationCardItem(IDynamicLocaleKey name, LucideIconKind icon, string route) : ObservableObject
    {
        public IDynamicLocaleKey Name { get; } = name;

        public LucideIconKind Icon { get; } = icon;

        public string Route { get; } = route;

        [ObservableProperty] public partial string? CountText { get; set; }
    }

    /// <summary>
    /// Recomputes the four quick configuration cards whenever assistants, plugins, functions, or skills change.
    /// </summary>
    private sealed class QuickConfigurationProvider : IDisposable
    {
        public IReadOnlyList<QuickConfigurationCardItem> Cards { get; } =
        [
            new(
                new DynamicLocaleKey(LocaleKey.HomePage_Assistants),
                LucideIconKind.Bot,
                MainViewNavigateMessage.CustomAssistantPageRoute),
            new(
                new DynamicLocaleKey(LocaleKey.HomePage_BuiltInTools),
                LucideIconKind.Hammer,
                MainViewNavigateMessage.ChatPluginPageRoute),
            new(
                new DynamicLocaleKey(LocaleKey.HomePage_Mcp),
                LucideIconKind.Unplug,
                MainViewNavigateMessage.ChatPluginPageRoute),
            new(
                new DynamicLocaleKey(LocaleKey.HomePage_Skills),
                LucideIconKind.Box,
                MainViewNavigateMessage.SkillPageRoute)
        ];

        private readonly Settings _settings;
        private readonly IChatPluginManager _chatPluginManager;
        private readonly ISkillManager _skillManager;
        private readonly CompositeDisposable _disposables = new();

        public QuickConfigurationProvider(
            Settings settings,
            IChatPluginManager chatPluginManager,
            ISkillManager skillManager)
        {
            _settings = settings;
            _chatPluginManager = chatPluginManager;
            _skillManager = skillManager;

            var subscription = Observable
                .Merge(
                    CreateAssistantChanges(),
                    CreateMcpChanges(),
                    CreateBuiltInToolChanges(),
                    CreateToolSettingsChanges(),
                    CreateSkillChanges())
                .StartWith(0)
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(_ => RefreshCards());
            _disposables.Add(subscription);
        }

        private IObservable<int> CreateAssistantChanges() =>
            _settings.Model.CustomAssistants
                .ToObservableChangeSet()
                .ToCollection()
                .Select(static _ => 0);

        private IObservable<int> CreateMcpChanges() =>
            _chatPluginManager.McpPlugins
                .ToObservableChangeSet<IReadOnlyBindableList<McpChatPlugin>, McpChatPlugin>()
                .ToCollection()
                .Select(static _ => 0);

        private IObservable<int> CreateBuiltInToolChanges()
        {
            var pluginChanges = _chatPluginManager.BuiltInPlugins
                .ToObservableChangeSet<IReadOnlyBindableList<BuiltInChatPlugin>, BuiltInChatPlugin>()
                .ToCollection()
                .Select(static _ => 0);

            var functionChanges = _chatPluginManager.BuiltInPlugins
                .ToObservableChangeSet<IReadOnlyBindableList<BuiltInChatPlugin>, BuiltInChatPlugin>()
                .MergeMany(static plugin => plugin.Functions
                    .ToObservableChangeSet<IReadOnlyBindableList<ChatFunction>, ChatFunction>()
                    .ToCollection()
                    .Select(static _ => 0));

            return pluginChanges.Merge(functionChanges);
        }

        private IObservable<int> CreateToolSettingsChanges() =>
            Observable
                .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    handler => _settings.Plugin.ToolEnablement.CollectionChanged += handler,
                    handler => _settings.Plugin.ToolEnablement.CollectionChanged -= handler)
                .Select(static _ => 0);

        private IObservable<int> CreateSkillChanges()
        {
            var groupChanges = _skillManager.SourceGroups
                .ToObservableChangeSet<IReadOnlyBindableList<SkillSourceGroup>, SkillSourceGroup>()
                .ToCollection()
                .Select(static _ => 0);

            var skillChanges = _skillManager.SourceGroups
                .ToObservableChangeSet<IReadOnlyBindableList<SkillSourceGroup>, SkillSourceGroup>()
                .MergeMany(static group => group.Skills
                    .ToObservableChangeSet<IReadOnlyBindableList<SkillDescriptor>, SkillDescriptor>()
                    .AutoRefresh(static skill => skill.IsEnabled)
                    .ToCollection()
                    .Select(static _ => 0));

            return groupChanges.Merge(skillChanges);
        }

        private void RefreshCards()
        {
            var assistantsCount = _settings.Model.CustomAssistants.Count;
            var mcpTotal = _chatPluginManager.McpPlugins.Count;
            var mcpEnabled = _chatPluginManager.McpPlugins.Count(plugin =>
                _settings.Plugin.ToolEnablement.IsPluginAllowed(plugin) ?? plugin.IsDefaultEnabled);

            var builtInFunctions = _chatPluginManager.BuiltInPlugins
                .SelectMany(static plugin => plugin.GetChatFunctions())
                .Where(static function => function.IsVisible)
                .ToArray();

            var skills = _skillManager.SourceGroups
                .SelectMany(static group => group.Skills)
                .Where(static skill => skill.IsValid)
                .ToArray();

            Cards[0].CountText = assistantsCount.ToString("N0", CultureInfo.CurrentCulture);
            Cards[1].CountText = FormatCapabilityCount(
                builtInFunctions.Length,
                _chatPluginManager.BuiltInPlugins.Sum(plugin => plugin.GetChatFunctions().Count(function =>
                    function.IsVisible &&
                    (_settings.Plugin.ToolEnablement.IsFunctionAllowed(plugin, function) ?? function.IsDefaultEnabled))));
            Cards[2].CountText = FormatCapabilityCount(mcpTotal, mcpEnabled);
            Cards[3].CountText = FormatCapabilityCount(skills.Length, skills.Count(static x => x.IsEnabled));
        }

        private static string FormatCapabilityCount(int totalCount, int enabledCount) =>
            $"{enabledCount.ToString("N0", CultureInfo.CurrentCulture)}/{totalCount.ToString("N0", CultureInfo.CurrentCulture)}";

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}