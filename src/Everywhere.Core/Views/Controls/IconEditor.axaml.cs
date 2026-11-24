using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Utilities;
using FuzzySharp;
using MessagePack;
using Serilog;
using Tabler.Avalonia;

namespace Everywhere.Views;

[MessagePackObject]
public readonly partial record struct EmojiEntry([property: Key(0)] string Emoji, [property: Key(1)] IReadOnlyList<string> Tags);

[TemplatePart(Name = "PART_IconTypeTabControl", Type = typeof(TabControl))]
[TemplatePart(Name = "PART_OutlineTabItem", Type = typeof(TabItem))]
[TemplatePart(Name = "PART_FilledTabItem", Type = typeof(TabItem))]
[TemplatePart(Name = "PART_EmojiTabItem", Type = typeof(TabItem))]
[TemplatePart(Name = "PART_OutlineListBox", Type = typeof(ListBox))]
[TemplatePart(Name = "PART_FilledListBox", Type = typeof(ListBox))]
public sealed class IconEditor : TemplatedControl
{
    public static readonly StyledProperty<ColoredIcon?> IconProperty = AvaloniaProperty.Register<IconEditor, ColoredIcon?>(nameof(Icon));

    public ColoredIcon? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DirectProperty<IconEditor, IReadOnlyBindableList<TablerIconKind>> OutlineItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<IconEditor, IReadOnlyBindableList<TablerIconKind>>(
            nameof(OutlineItemsSource),
            o => o.OutlineItemsSource);

    public IReadOnlyBindableList<TablerIconKind> OutlineItemsSource { get; }

    public static readonly DirectProperty<IconEditor, IReadOnlyBindableList<TablerIconKind>> FilledItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<IconEditor, IReadOnlyBindableList<TablerIconKind>>(
            nameof(FilledItemsSource),
            o => o.FilledItemsSource);

    public IReadOnlyBindableList<TablerIconKind> FilledItemsSource { get; }

    public static readonly DirectProperty<IconEditor, IReadOnlyBindableList<string>> EmojiItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<IconEditor, IReadOnlyBindableList<string>>(
            nameof(EmojiItemsSource),
            o => o.EmojiItemsSource);

    public IReadOnlyBindableList<string> EmojiItemsSource { get; }

    public static readonly StyledProperty<string?> QueryTextProperty = AvaloniaProperty.Register<IconEditor, string?>(nameof(QueryText));

    public string? QueryText
    {
        get => GetValue(QueryTextProperty);
        set => SetValue(QueryTextProperty, value);
    }

    private static readonly (TablerIconKind Kind, string Name, bool HasFilled)[] CachedTablerIcons =
        Enum.GetValues<TablerIconKind>()
            .AsValueEnumerable()
            .Select(x => (Kind: x, Name: x.ToString(), HasFilled: x.HasFilled()))
            .ToArray();

    private readonly BindableList<TablerIconKind> _outlineItemsSource = [];
    private readonly BindableList<TablerIconKind> _filledItemsSource = [];
    private readonly BindableList<string> _emojiItemsSource = [];

    private SingleAssignmentDisposable? _queryTextChangedSubscription;
    private IDisposable? _iconTypeTabControlSelectionChangedSubscription;
    private IDisposable? _outlineListSelectionChangedSubscription;
    private IDisposable? _filledListSelectionChangedSubscription;
    private TabControl? _iconTypeTabControl;
    private TabItem? _outlineTabItem;
    private TabItem? _filledTabItem;
    private TabItem? _emojiTabItem;
    private bool _isSyncingUi;

    public IconEditor()
    {
        OutlineItemsSource = _outlineItemsSource;
        FilledItemsSource = _filledItemsSource;
        EmojiItemsSource = _emojiItemsSource;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);

        if (_queryTextChangedSubscription is not null) return;

        SingleAssignmentDisposable subscription;
        _queryTextChangedSubscription = subscription = new SingleAssignmentDisposable();
        InitializeQueryTextSubscriptionAsync(subscription).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposeHelper.DisposeToDefault(ref _queryTextChangedSubscription);
        base.OnDetachedFromVisualTree(e);
    }

    private async Task InitializeQueryTextSubscriptionAsync(SingleAssignmentDisposable subscription)
    {
        await EmojiSearchEngine.Shared.LoadDictionariesAsync(LocaleManager.CurrentLocale).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_queryTextChangedSubscription, subscription))
            {
                return;
            }

            subscription.Disposable = this.GetObservable(QueryTextProperty)
                .Select(q => q ?? string.Empty)
                .DistinctUntilChanged()
                .Throttle(TimeSpan.FromMilliseconds(200))
                .Select(query => Observable.FromAsync(token => PerformSearchAsync(query, token)))
                .Switch()
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(ApplySearchResults);
        });
    }

    private void ApplySearchResults((IEnumerable<TablerIconKind> Outline, IEnumerable<TablerIconKind> Filled, IEnumerable<string> Emoji) results)
    {
        _isSyncingUi = true;
        try
        {
            var currentKind = Icon?.Kind;
            _outlineItemsSource.Reset(currentKind is { } outlineKind && Enum.IsDefined(outlineKind)
                ? [outlineKind, .. results.Outline.Where(item => item != outlineKind)]
                : [.. results.Outline]);
            _filledItemsSource.Reset(currentKind is { } filledKind && filledKind.HasFilled()
                ? [filledKind, .. results.Filled.Where(item => item != filledKind)]
                : [.. results.Filled]);
            _emojiItemsSource.Reset(Icon?.Text is { } text
                ? [text, .. results.Emoji.Where(item => !item.Equals(text, StringComparison.OrdinalIgnoreCase))]
                : [.. results.Emoji]);
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private async static Task<(IEnumerable<TablerIconKind> Outline, IEnumerable<TablerIconKind> Filled, IEnumerable<string> Emoji)> PerformSearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            var outlineResults = CachedTablerIcons.Select(x => x.Kind);
            var filledResults = CachedTablerIcons.Where(x => x.HasFilled).Select(x => x.Kind);
            return (outlineResults, filledResults, EmojiSearchEngine.Shared.GetDefaultView());
        }

        return await Task.Run(
            () =>
            {
                var matched = CachedTablerIcons
                    .Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var outlineResults = matched.Select(x => x.Kind);
                var filledResults = matched.Where(x => x.HasFilled).Select(x => x.Kind);
                var emojiResults = EmojiSearchEngine.Shared.Search(query, cancellationToken: cancellationToken);

                return (outlineResults, filledResults, emojiResults);
            },
            cancellationToken);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _iconTypeTabControlSelectionChangedSubscription?.Dispose();
        _outlineListSelectionChangedSubscription?.Dispose();
        _filledListSelectionChangedSubscription?.Dispose();

        _iconTypeTabControl = e.NameScope.Find<TabControl>("PART_IconTypeTabControl").NotNull();
        _outlineTabItem = e.NameScope.Find<TabItem>("PART_OutlineTabItem").NotNull();
        _filledTabItem = e.NameScope.Find<TabItem>("PART_FilledTabItem").NotNull();
        _emojiTabItem = e.NameScope.Find<TabItem>("PART_EmojiTabItem").NotNull();
        var outlineListBox = e.NameScope.Find<ListBox>("PART_OutlineListBox").NotNull();
        var filledListBox = e.NameScope.Find<ListBox>("PART_FilledListBox").NotNull();

        _iconTypeTabControlSelectionChangedSubscription = _iconTypeTabControl.AddDisposableHandler(
            SelectingItemsControl.SelectionChangedEvent,
            (_, args) =>
            {
                if (_isSyncingUi || !ReferenceEquals(args.Source, _iconTypeTabControl) || Icon is not { } icon)
                {
                    return;
                }

                if (args.AddedItems is not [TabItem tabItem])
                {
                    return;
                }

                if (ReferenceEquals(tabItem, _outlineTabItem))
                {
                    icon.Type = ColoredIconType.Tabler;
                    icon.IsFilled = false;
                }
                else if (ReferenceEquals(tabItem, _filledTabItem))
                {
                    icon.Type = ColoredIconType.Tabler;
                    if (icon.Kind.HasFilled())
                    {
                        icon.IsFilled = true;
                    }
                }
                else if (ReferenceEquals(tabItem, _emojiTabItem))
                {
                    icon.Type = ColoredIconType.Text;
                }
            });

        _outlineListSelectionChangedSubscription = outlineListBox.AddDisposableHandler(
            SelectingItemsControl.SelectionChangedEvent,
            (_, args) =>
            {
                if (_isSyncingUi || Icon is not { } icon || args.AddedItems is not [TablerIconKind])
                {
                    return;
                }

                icon.Type = ColoredIconType.Tabler;
                icon.IsFilled = false;
            });

        _filledListSelectionChangedSubscription = filledListBox.AddDisposableHandler(
            SelectingItemsControl.SelectionChangedEvent,
            (_, args) =>
            {
                if (_isSyncingUi || Icon is not { } icon || args.AddedItems is not [TablerIconKind])
                {
                    return;
                }

                icon.Type = ColoredIconType.Tabler;
                icon.IsFilled = true;
            });

        SetIconTypeTabControlSelection(Icon);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconProperty)
        {
            SetIconTypeTabControlSelection(Icon);
        }
    }

    private void SetIconTypeTabControlSelection(ColoredIcon? icon)
    {
        if (_iconTypeTabControl is null)
        {
            return;
        }

        _isSyncingUi = true;
        try
        {
            _iconTypeTabControl.SelectedItem = icon?.Type switch
            {
                ColoredIconType.Text => _emojiTabItem,
                _ when icon?.IsFilled == true => _filledTabItem,
                _ => _outlineTabItem
            };
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private sealed class EmojiSearchEngine
    {
        public static EmojiSearchEngine Shared { get; } = new();

        private volatile IReadOnlyList<EmojiEntry> _database = [];
        private LocaleName? _lastLoadedLocale;
        private readonly SemaphoreSlim _loadLock = new(1, 1);

        public async Task LoadDictionariesAsync(LocaleName locale)
        {
            if (_lastLoadedLocale == locale) return;

            await _loadLock.WaitAsync();
            try
            {
                if (_lastLoadedLocale == locale) return;

                var newDatabase = await Task.Run(() =>
                {
                    var defaultUri = new Uri("avares://Everywhere.Core/Assets/EmojiBase/En.bin");
                    var defaultData = LoadEmojiEntries(defaultUri);
                    if (defaultData is null)
                    {
                        Log.ForContext<EmojiSearchEngine>().Error("Failed to load default emoji dictionary from {Uri}", defaultUri);
                        return null;
                    }
                    if (locale == LocaleName.En)
                    {
                        return defaultData;
                    }

                    var localizedUri = new Uri(
                        $"avares://Everywhere.Core/Assets/EmojiBase/{locale switch {
                            LocaleName.ZhHans => "Zh",
                            LocaleName.ZhHantHk => "ZhHant",
                            _ => locale.ToString()
                        }}.bin");
                    var localizedData = LoadEmojiEntries(localizedUri);
                    if (localizedData is null)
                    {
                        return defaultData;
                    }

                    var newDatabase = new List<EmojiEntry>(defaultData.Count);
                    var defaultDictionary = defaultData.AsValueEnumerable().ToDictionary(x => x.Emoji, x => x);
                    foreach (var entry in localizedData)
                    {
                        var combinedTags = new List<string>(entry.Tags);
                        if (defaultDictionary.TryGetValue(entry.Emoji, out var enItem))
                        {
                            combinedTags.AddRange(enItem.Tags);
                        }
                        newDatabase.Add(
                            entry with
                            {
                                Tags = combinedTags
                            });
                    }

                    return newDatabase;
                });

                if (newDatabase != null)
                {
                    _database = newDatabase;
                    _lastLoadedLocale = locale;
                }
            }
            finally
            {
                _loadLock.Release();
            }
        }

        public IEnumerable<string> GetDefaultView() => _database.Select(e => e.Emoji);

        public IEnumerable<string> Search(string query, int limit = 50, CancellationToken cancellationToken = default)
        {
            query = query.Trim();
            var results = new List<(string Emoji, int Score)>(limit);
            var checkCounter = 0;

            foreach (var entry in _database)
            {
                if ((++checkCounter & 127) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (entry.Emoji.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add((entry.Emoji, 100));
                    continue;
                }

                var maxScore = 0;
                foreach (var tag in entry.Tags)
                {
                    if (tag.Equals(query, StringComparison.OrdinalIgnoreCase))
                    {
                        maxScore = 100;
                        break;
                    }

                    if (tag.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    {
                        maxScore = Math.Max(maxScore, 90);
                    }
                    else if (tag.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        maxScore = Math.Max(maxScore, 80);
                    }
                    else
                    {
                        var fuzzyScore = Fuzz.PartialRatio(query, tag);
                        if (fuzzyScore > 60)
                        {
                            maxScore = Math.Max(maxScore, (int)(fuzzyScore * 0.8));
                        }
                    }
                }

                if (maxScore > 0)
                {
                    results.Add((entry.Emoji, maxScore));
                }
            }

            return results
                .AsValueEnumerable()
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .Select(x => x.Emoji)
                .ToArray();
        }

        private static IReadOnlyList<EmojiEntry>? LoadEmojiEntries(Uri uri)
        {
            if (!AssetLoader.Exists(uri)) return null;

            try
            {
                using var stream = AssetLoader.Open(uri);
                return MessagePackSerializer.Deserialize<IReadOnlyList<EmojiEntry>>(stream);
            }
            catch (Exception ex)
            {
                Log.ForContext<EmojiSearchEngine>().Error(ex, "Failed to load emoji dictionary from {Uri}", uri);
                return null;
            }
        }
    }
}
