using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
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
using Lucide.Avalonia;
using MessagePack;
using Serilog;
using ZLinq;

namespace Everywhere.Views;

[MessagePackObject]
public readonly partial record struct EmojiEntry([property: Key(0)] string Emoji, [property: Key(1)] IReadOnlyList<string> Tags);

[TemplatePart(Name = "PART_IconTypeTabControl", Type = typeof(TabControl))]
public sealed class IconEditor : TemplatedControl
{
    public static readonly StyledProperty<ColoredIcon?> IconProperty = AvaloniaProperty.Register<IconEditor, ColoredIcon?>(nameof(Icon));

    public ColoredIcon? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DirectProperty<IconEditor, IReadOnlyBindableList<LucideIconKind>> LucideItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<IconEditor, IReadOnlyBindableList<LucideIconKind>>(
            nameof(LucideItemsSource),
            o => o.LucideItemsSource);

    public IReadOnlyBindableList<LucideIconKind> LucideItemsSource { get; }

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

    private static readonly IReadOnlyList<(LucideIconKind Kind, string Name)> CachedLucideIcons =
        Enum.GetValues<LucideIconKind>()
            .AsValueEnumerable()
            .Select(x => (Kind: x, Name: x.ToString()))
            .ToList();

    private readonly BindableList<LucideIconKind> _lucideItemsSource = [];
    private readonly BindableList<string> _emojiItemsSource = [];

    private CompositeDisposable? _subscriptions;
    private IDisposable? _iconTypeTabControlSelectionChangedSubscription;
    private TabControl? _iconTypeTabControl;

    public IconEditor()
    {
        LucideItemsSource = _lucideItemsSource;
        EmojiItemsSource = _emojiItemsSource;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);

        if (_subscriptions is not null) return;

        var subscriptions = new CompositeDisposable();
        _subscriptions = subscriptions;

        EmojiSearchEngine.Shared.LoadDictionariesAsync(LocaleManager.CurrentLocale).ContinueWith(_ =>
        {
            if (_subscriptions != subscriptions) return;

            this.GetObservable(QueryTextProperty)
                .Select(q => q ?? string.Empty)
                .DistinctUntilChanged()
                .Throttle(TimeSpan.FromMilliseconds(200))
                .Select(query => Observable.FromAsync(token => PerformSearchAsync(query, token)))
                .Switch()
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(results =>
                {
                    _lucideItemsSource.Clear();
                    if (Icon?.Kind is { } kind)
                    {
                        _lucideItemsSource.Add(kind);
                        foreach (var lucide in results.Lucide.Where(k => k != kind))
                        {
                            _lucideItemsSource.Add(lucide);
                        }
                    }
                    else
                    {
                        foreach (var lucide in results.Lucide)
                        {
                            _lucideItemsSource.Add(lucide);
                        }
                    }

                    _emojiItemsSource.Clear();
                    if (Icon?.Text is { } text)
                    {
                        _emojiItemsSource.Add(text);
                        foreach (var emoji in results.Emoji.Where(e => !e.Equals(text, StringComparison.OrdinalIgnoreCase)))
                        {
                            _emojiItemsSource.Add(emoji);
                        }
                    }
                    else
                    {
                        foreach (var emoji in results.Emoji)
                        {
                            _emojiItemsSource.Add(emoji);
                        }
                    }
                })
                .DisposeWith(subscriptions);
        }).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposeHelper.DisposeToDefault(ref _subscriptions);
        DisposeHelper.DisposeToDefault(ref _iconTypeTabControlSelectionChangedSubscription);
        _iconTypeTabControl = null;

        base.OnDetachedFromVisualTree(e);
    }

    private async Task<(IEnumerable<LucideIconKind> Lucide, IEnumerable<string> Emoji)> PerformSearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            return (CachedLucideIcons.Select(x => x.Kind), EmojiSearchEngine.Shared.GetDefaultView());
        }

        return await Task.Run(
            () =>
            {
                var lucideResults = CachedLucideIcons
                    .Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Kind);
                var emojiResults = EmojiSearchEngine.Shared.Search(query, cancellationToken: cancellationToken);

                return (lucideResults, emojiResults);
            },
            cancellationToken);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _iconTypeTabControlSelectionChangedSubscription?.Dispose();
        _iconTypeTabControl = e.NameScope.Find<TabControl>("PART_IconTypeTabControl").NotNull();
        _iconTypeTabControlSelectionChangedSubscription = _iconTypeTabControl.AddDisposableHandler(
            SelectingItemsControl.SelectionChangedEvent,
            (_, args) =>
            {
                if (args.AddedItems is [TabItem { Tag: ColoredIconType type }]) Icon?.Type = type;
            },
            handledEventsToo: true);
        SetIconTypeTabControlSelection(Icon?.Type ?? ColoredIconType.Lucide);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconProperty)
        {
            SetIconTypeTabControlSelection(Icon?.Type ?? ColoredIconType.Lucide);
        }
    }

    private void SetIconTypeTabControlSelection(ColoredIconType type)
    {
        if (_iconTypeTabControl?.Items is not IEnumerable items) return;
        var tabItem = items.OfType<TabItem>().FirstOrDefault(ti => ti.Tag is ColoredIconType t && t == type);
        if (tabItem != null) _iconTypeTabControl.SelectedItem = tabItem;
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
                .ToList();
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
