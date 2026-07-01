using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;

namespace Everywhere.I18N;

public interface ILocaleResourceProvider
{
    ResourceDictionary GetResources(LocaleName locale);
}

public sealed class LocaleManager : ResourceDictionary
{
    public static LocaleManager Shared => _shared ?? throw new InvalidOperationException("LocaleManager is not initialized.");

    private static readonly List<Func<ILocaleResourceProvider>> ProviderFactories = [];
    private static readonly List<ILocaleResourceProvider> Providers = [];
    private static readonly Lock SyncRoot = new();

    private static LocaleManager? _shared;
    private static LocaleName? _currentLocale;

    public LocaleManager()
    {
        if (_shared is not null) throw new InvalidOperationException("LocaleManager is already initialized.");

        _shared = this;
        CurrentLocale = GetCurrentCultureLocale();
    }

    public static LocaleName CurrentLocale
    {
        get => _currentLocale.GetValueOrDefault();
        set => Dispatcher.UIThread.Invoke(() =>
        {
            if (_currentLocale == value) return;

            var oldLocale = _currentLocale;
            _currentLocale = value;
            _shared?.ApplyLocale(value);

            WeakReferenceMessenger.Default.Send(new LocaleChangedMessage(oldLocale, value));
        });
    }

    public static void RegisterProvider(Func<ILocaleResourceProvider> providerFactory)
    {
        LocaleManager? shared;
        lock (SyncRoot)
        {
            ProviderFactories.Add(providerFactory);
            shared = _shared;
        }

        shared?.ApplyLocale(CurrentLocale);
    }

    private void ApplyLocale(LocaleName locale)
    {
        var dispatcher = Dispatcher.UIThread;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyLocale(locale));
            return;
        }

        lock (SyncRoot)
        {
            while (Providers.Count < ProviderFactories.Count)
            {
                Providers.Add(ProviderFactories[Providers.Count]());
            }

            MergedDictionaries.Clear();
            foreach (var provider in Providers)
            {
                MergedDictionaries.Add(provider.GetResources(locale));
            }
        }
    }

    private static LocaleName GetCurrentCultureLocale()
    {
        var cultureInfo = CultureInfo.CurrentUICulture;
        while (!string.IsNullOrEmpty(cultureInfo.Name))
        {
            if (Enum.TryParse<LocaleName>(cultureInfo.Name.Replace("-", ""), true, out var locale))
            {
                return locale;
            }

            cultureInfo = cultureInfo.Parent;
        }

        return default;
    }
}
