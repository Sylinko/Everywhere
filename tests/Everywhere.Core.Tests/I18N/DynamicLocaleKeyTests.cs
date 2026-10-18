using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MessagePack;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.I18N;

namespace Everywhere.Core.Tests.I18N;

[TestFixture]
[NonParallelizable]
public sealed class DynamicLocaleKeyTests
{
    [AvaloniaTest]
    public async Task BackgroundLanguageChange_NotifiesDynamicBindingsOnUiThread()
    {
        var oldLocale = LocaleManager.CurrentLocale;
        LocaleManager.CurrentLocale = LocaleName.En;
        try
        {
            using var registration = LocaleManager.RegisterProvider(typeof(DynamicLocaleKeyTests).Assembly, () => new TestProvider());
            var observer = new RecordingObserver();
            using var subscription = new DynamicLocaleKey("DynamicProbe").Subscribe(observer);
            await Task.Run(() => LocaleManager.CurrentLocale = LocaleName.ZhHans).WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.That(observer.Values, Is.EqualTo(new[] { "English", "Chinese" }));
        }
        finally
        {
            LocaleManager.CurrentLocale = oldLocale;
        }
    }

    [AvaloniaTest]
    public void ProviderReplacement_DoesNotNotifyBinding_UntilLanguageChanges()
    {
        var oldLocale = LocaleManager.CurrentLocale;
        LocaleManager.CurrentLocale = LocaleName.En;
        try
        {
            using var registration = LocaleManager.RegisterProvider(typeof(DynamicLocaleKeyTests).Assembly, () => new TestProvider());
            var observer = new RecordingObserver();
            using var subscription = new DynamicLocaleKey("DynamicProbe").Subscribe(observer);
            using var replacement = LocaleManager.RegisterProvider(typeof(DynamicLocaleKeyTests).Assembly, () => new TestProvider("replacement"));
            Assert.Multiple(() =>
            {
                Assert.That("DynamicProbe".I18N(), Is.EqualTo("replacement"));
                Assert.That(observer.Values, Is.EqualTo(new[] { "English" }));
            });
            LocaleManager.CurrentLocale = LocaleName.ZhHans;
            Assert.That(observer.Values, Is.EqualTo(new[] { "English", "replacement" }));
        }
        finally
        {
            LocaleManager.CurrentLocale = oldLocale;
        }
    }

    [AvaloniaTest]
    public async Task JsonLocaleKey_BackgroundLanguageChange_NotifiesOnUiThread()
    {
        var oldLocale = LocaleManager.CurrentLocale;
        LocaleManager.CurrentLocale = LocaleName.En;
        try
        {
            var key = new JsonDynamicLocaleKey { ["en"] = "English", ["zh-hans"] = "Chinese" };
            var observer = new RecordingObserver();
            using var subscription = key.Subscribe(observer);
            await Task.Run(() => LocaleManager.CurrentLocale = LocaleName.ZhHans).WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.That(observer.Values, Is.EqualTo(new[] { "English", "Chinese" }));
        }
        finally
        {
            LocaleManager.CurrentLocale = oldLocale;
        }
    }

    [Test]
    public void JsonLocaleKey_MixedCaseKeys_PreservesEqualityAcrossSerialization()
    {
        var original = new JsonDynamicLocaleKey { ["EN"] = "English", ["zh-Hans"] = "中文" };
        var reordered = new JsonDynamicLocaleKey(2) { ["ZH-HANS"] = "中文", ["en"] = "English" };
        var copied = new JsonDynamicLocaleKey(original);
        var json = JsonSerializer.Deserialize<JsonDynamicLocaleKey>(JsonSerializer.Serialize(original))
            ?? throw new AssertionException("Missing deserialized language dictionary.");
        var messagePack = MessagePackSerializer.Deserialize<JsonDynamicLocaleKey>(MessagePackSerializer.Serialize(original));

        foreach (var key in new[] { reordered, copied, json, messagePack })
        {
            Assert.That(key, Is.Not.Null);
            Assert.That(original.Equals(key), Is.True);
            Assert.That(key.Equals(original), Is.True);
            Assert.That(key.GetHashCode(), Is.EqualTo(original.GetHashCode()));
            Assert.That(key["zh-hans"], Is.EqualTo("中文"));
        }

        reordered["en"] = "english";
        Assert.That(original.Equals(reordered), Is.False);
    }

    [AvaloniaTest]
    public void JsonLocaleKey_MixedCaseTags_UsesParentAndEnglishFallback()
    {
        var oldLocale = LocaleManager.CurrentLocale;
        try
        {
            var key = new JsonDynamicLocaleKey { ["EN"] = "English", ["zh-Hant"] = "繁體" };
            LocaleManager.CurrentLocale = LocaleName.ZhHantHk;
            Assert.That(key.ToString(), Is.EqualTo("繁體"));
            LocaleManager.CurrentLocale = LocaleName.De;
            Assert.That(key.ToString(), Is.EqualTo("English"));
        }
        finally
        {
            LocaleManager.CurrentLocale = oldLocale;
        }
    }

    private sealed class RecordingObserver : IObserver<object?>
    {
        public List<object?> Values { get; } = [];
        public void OnNext(object? value)
        {
            Dispatcher.UIThread.VerifyAccess();
            Values.Add(value);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }

    private sealed class TestProvider(string? replacement = null) : ILocaleResourceProvider
    {
        public bool TryGetString(string key, LocaleName locale, [NotNullWhen(true)] out string? value)
        {
            value = key == "DynamicProbe" ? replacement ?? (locale == LocaleName.En ? "English" : "Chinese") : null;
            return value is not null;
        }
    }
}
