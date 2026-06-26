using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reactive;
using System.Reactive.Disposables;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using MessagePack;
using MessagePack.Formatters;
using ZLinq;

namespace Everywhere.I18N;

/// <summary>
/// MessagePack serializable base class for dynamic resource keys. Make them happy.
/// </summary>
[Union(0, typeof(DynamicLocaleKey))]
[Union(1, typeof(DirectLocaleKey))]
[Union(2, typeof(FormattedDynamicLocaleKey))]
[Union(3, typeof(AggregateDynamicLocaleKey))]
public partial interface IDynamicLocaleKey : IObservable<object?>;

/// <summary>
/// This class is used to create a dynamic resource key for axaml Binding.
/// </summary>
/// <param name="key"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class DynamicLocaleKey(object? key) : IDynamicLocaleKey, IRecipient<LocaleChangedMessage>
{
    [Key(0)]
    public object Key { get; } = key ?? string.Empty; // avoid null key (especially for MessagePack)

    [IgnoreMember] private readonly Dictionary<int, IObserver<object?>> _observers = new(1); // usually only one subscriber

    /// <summary>
    /// Subscribes an observer to receive updates when the locale changes.
    /// </summary>
    /// <remarks>
    /// The Avalonia's implementation of IObservable (GetResourceObservable) has issues which can cause memory leaks.
    /// It holds strong references to observers, preventing them from being garbage collected.
    /// This implementation uses weak references to avoid memory leaks.
    /// Also brings better performance by avoiding unnecessary resource lookups when there are no subscribers.
    /// </remarks>
    /// <param name="observer"></param>
    /// <returns></returns>
    public virtual IDisposable Subscribe(IObserver<object?> observer)
    {
        // Only allow subscription on UI thread
        Dispatcher.UIThread.VerifyAccess();

        var id = _observers.Count;
        if (id == 0)
        {
            WeakReferenceMessenger.Default.Register(this); // register for locale change messages
        }

        while (_observers.ContainsKey(id)) id++; // ensure unique id

        _observers.Add(id, observer);
        observer.OnNext(ToString());

        return Disposable.Create(() =>
        {
            _observers.Remove(id);
            if (_observers.Count == 0)
            {
                WeakReferenceMessenger.Default.Unregister<LocaleChangedMessage>(this);
            }
        });
    }

    [return: NotNullIfNotNull(nameof(key))]
    public static implicit operator DynamicLocaleKey?(string? key) => key == null ? null : new DynamicLocaleKey(key);

    public static bool Exists(object key) => LocaleManager.Shared.TryGetResource(key, null, out _);

    public static bool TryResolve(object key, [NotNullWhen(true)] out string? result)
    {
        if (LocaleManager.Shared.TryGetResource(key, null, out var resource))
        {
            result = resource?.ToString() ?? string.Empty;
            return true;
        }

        result = null;
        return false;
    }

    public static string Resolve(object? key)
    {
        if (key is not null && LocaleManager.Shared.TryGetResource(key, null, out var resource))
        {
            return resource?.ToString() ?? string.Empty;
        }

        return key?.ToString() ?? string.Empty;
    }

    public void Receive(LocaleChangedMessage message)
    {
        foreach (var observer in _observers.Values.AsValueEnumerable())
        {
            observer.OnNext(ToString());
        }
    }

    public override string? ToString() => Resolve(Key);

    public override bool Equals(object? obj) => obj is DynamicLocaleKey other && Equals(Key, other.Key);

    public override int GetHashCode() => Key.GetHashCode();
}

/// <summary>
/// Directly wraps a raw string for use in axaml.
/// This is useful for cases where you want to use a string as a resource key without any formatting or dynamic behavior.
/// </summary>
/// <param name="key"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class DirectLocaleKey(object? key) : DynamicLocaleKey(key)
{
    public static DirectLocaleKey Empty { get; } = new(null);

    private static readonly IDisposable NullDisposable = Disposable.Empty;

    public override IDisposable Subscribe(IObserver<object?> observer)
    {
        observer.OnNext(Key);
        return NullDisposable;
    }

    /// <summary>
    /// For direct resource key, just return the key itself (even if it's null).
    /// </summary>
    /// <returns></returns>
    public override string? ToString() => Key.ToString();

    public override bool Equals(object? obj) => obj is DynamicLocaleKey other && Equals(Key, other.Key);

    public override int GetHashCode() => Key.GetHashCode();
}

/// <summary>
/// This class is used to create a dynamic resource key for axaml Binding with formatted arguments.
/// It first resolves the resource key, then formats it with the provided arguments.
/// Arguments will be also resolved if they are dynamic resource keys.
/// </summary>
/// <param name="key"></param>
/// <param name="args"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class FormattedDynamicLocaleKey(object key, params IReadOnlyList<IDynamicLocaleKey?> args) : DynamicLocaleKey(key)
{
    [Key(1)]
    private IReadOnlyList<IDynamicLocaleKey?> Args { get; } = args;

    public override IDisposable Subscribe(IObserver<object?> observer)
    {
        var formatter = new AnonymousObserver<object?>(_ => observer.OnNext(ToString()));
        var disposables = new CompositeDisposable();
        disposables.Add(base.Subscribe(formatter));
        foreach (var arg in Args.AsValueEnumerable().OfType<IObservable<object?>>()) disposables.Add(arg.Subscribe(formatter));
        return disposables;
    }

    public override string ToString()
    {
        var resolvedKey = Resolve(Key);
        try
        {
            return string.IsNullOrEmpty(resolvedKey) ?
                string.Empty :
                string.Format(resolvedKey, Args.AsValueEnumerable().Select(object? (a) => a?.ToString()).ToArray());
        }
        catch (FormatException e)
        {
            Debug.Fail("Failed to format resource key. Check if the number of arguments matches the placeholders in the resource string.");

            // If formatting fails, return the resolved key without formatting to avoid breaking the UI.
            // This can happen if the number of arguments does not match the placeholders in the resource string.
            Console.Error.WriteLine($"Failed to format resource key '{resolvedKey}' with arguments [{string.Join(", ", Args)}]: {e}");
            return resolvedKey;
        }
    }

    public override bool Equals(object? obj) => obj is FormattedDynamicLocaleKey other &&
        Equals(Key, other.Key) &&
        Args.SequenceEqual(other.Args);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Key);
        foreach (var arg in Args) hash.Add(arg);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Aggregates multiple dynamic resource keys into one.
/// </summary>
/// <param name="keys"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class AggregateDynamicLocaleKey(IReadOnlyList<IDynamicLocaleKey> keys, string separator = ", ") : IDynamicLocaleKey
{
    [Key(0)]
    private IReadOnlyList<IDynamicLocaleKey> Keys { get; } = keys;

    [Key(1)]
    private string Separator { get; } = separator;

    public IDisposable Subscribe(IObserver<object?> observer)
    {
        var formatter = new AnonymousObserver<object?>(_ => observer.OnNext(ToString()));
        var disposables = new CompositeDisposable();
        foreach (var key in Keys.AsValueEnumerable().OfType<IDynamicLocaleKey>()) disposables.Add(key.Subscribe(formatter));
        return disposables;
    }

    public override string ToString()
    {
        if (Keys is not { Count: > 0 })
        {
            return string.Empty;
        }

        var resolvedKeys = new object?[Keys.Count];
        for (var i = 0; i < Keys.Count; i++)
        {
            if (Keys[i] is DynamicLocaleKey dynamicKey) resolvedKeys[i] = dynamicKey.ToString();
            else resolvedKeys[i] = Keys[i];
        }

        return string.Join(Separator, resolvedKeys);
    }

    public override bool Equals(object? obj) => obj is AggregateDynamicLocaleKey other &&
        Keys.SequenceEqual(other.Keys) &&
        Separator == other.Separator;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Keys) hash.Add(key);
        hash.Add(Separator);
        return hash.ToHashCode();
    }
}

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
[MessagePackFormatter(typeof(MessagePackFormatter))]
public sealed partial class JsonDynamicLocaleKey : Dictionary<string, string>, IDynamicLocaleKey, IRecipient<LocaleChangedMessage>
{
    [IgnoreMember] private readonly Dictionary<int, IObserver<object?>> _observers = new(1); // usually only one subscriber

    /// <summary>
    /// ZhHantHk -> ["zh-hant-hk", "zh-hant", "zh"]
    /// En -> ["en"]
    /// </summary>
    private static readonly Dictionary<LocaleName, string[]> FallbackCache;

    static JsonDynamicLocaleKey()
    {
        FallbackCache = new Dictionary<LocaleName, string[]>();
        foreach (var locale in Enum.GetValues<LocaleName>())
        {
            // ZhHantHk -> zh-hant-hk
            var hyphenated = PascalCaseRegex().Replace(locale.ToString(), "-").ToLowerInvariant();
            var fallbacks = new List<string> { hyphenated };

            // zh-hant-hk -> zh-hant -> zh
            var lastHyphen = hyphenated.LastIndexOf('-');
            while (lastHyphen > 0)
            {
                hyphenated = hyphenated[..lastHyphen];
                fallbacks.Add(hyphenated);
                lastHyphen = hyphenated.LastIndexOf('-');
            }

            FallbackCache[locale] = fallbacks.ToArray();
        }
    }

    public JsonDynamicLocaleKey() { }

    public JsonDynamicLocaleKey(int capacity) : base(capacity) { }

    public JsonDynamicLocaleKey(IEnumerable<KeyValuePair<string, string>> init) : base(init) { }

    /// <summary>
    /// Subscribes an observer to receive updates when the locale changes.
    /// </summary>
    /// <remarks>
    /// The Avalonia's implementation of IObservable (GetResourceObservable) has issues which can cause memory leaks.
    /// It holds strong references to observers, preventing them from being garbage collected.
    /// This implementation uses weak references to avoid memory leaks.
    /// Also brings better performance by avoiding unnecessary resource lookups when there are no subscribers.
    /// </remarks>
    /// <param name="observer"></param>
    /// <returns></returns>
    public IDisposable Subscribe(IObserver<object?> observer)
    {
        // Only allow subscription on UI thread
        Dispatcher.UIThread.VerifyAccess();

        var id = _observers.Count;
        if (id == 0)
        {
            WeakReferenceMessenger.Default.Register(this); // register for locale change messages
        }

        while (_observers.ContainsKey(id)) id++; // ensure unique id

        _observers.Add(id, observer);
        observer.OnNext(ToString());

        return Disposable.Create(() =>
        {
            _observers.Remove(id);
            if (_observers.Count == 0)
            {
                WeakReferenceMessenger.Default.Unregister<LocaleChangedMessage>(this);
            }
        });
    }

    public void Receive(LocaleChangedMessage message)
    {
        foreach (var observer in _observers.Values.AsValueEnumerable())
        {
            observer.OnNext(ToString());
        }
    }

    public override string? ToString()
    {
        // 1. Try match target locales
        if (FallbackCache.TryGetValue(LocaleManager.CurrentLocale, out var fallbacks))
        {
            foreach (var key in fallbacks)
            {
                if (TryGetValue(key, out var value))
                {
                    return value;
                }
            }
        }

        // 2. Fallback to "en" and first value
        return TryGetValue("en", out var enValue) ? enValue : Values.FirstOrDefault();
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex PascalCaseRegex();

    public class MessagePackFormatter : DictionaryFormatterBase<string, string, JsonDynamicLocaleKey>
    {
        protected override JsonDynamicLocaleKey Create(int count, MessagePackSerializerOptions options) => new(count);

        protected override void Add(JsonDynamicLocaleKey collection, int index, string key, string value, MessagePackSerializerOptions options) =>
            collection.Add(key, value);
    }
}