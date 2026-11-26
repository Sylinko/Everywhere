using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Data.Core;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Chat.Plugins;
using Everywhere.Core.I18N;
using Everywhere.I18N;
using Everywhere.MarkupExtensions;
using Everywhere.Terminal;
using Everywhere.Views;
using NSubstitute;
using ShadUI;

namespace Everywhere.Core.Tests.I18N;

[TestFixture]
[NonParallelizable]
public sealed class I18NExtensionTests
{
    [AvaloniaTest]
    public void SingleStepCompiledArgument_SourceCountChanges_UsesTypedPathAndFormatsOnceAtStartup()
    {
        var source = new ObservableCollection<string> { "one" };
        var target = new TextBlock { DataContext = source };
        var property = new ClrPropertyInfo<ObservableCollection<string>, int>(nameof(source.Count), x => x.Count, null);
        // This is the generic path element emitted by the XAML compiler for a single CLR property.
        // Its fallback accessor must not be needed when the expression can read the property directly.
        var path = new CompiledBindingPathBuilder().Property(
            property, (_, _) => throw new AssertionException("Expected a typed binding expression."), false).Build();
        var format = new CountingFormat();
        var published = new List<object?>();
        target.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToolTip.TipProperty) published.Add(ToolTip.GetTip(target));
        };
        using var binding = BindLocalized(target, ToolTip.TipProperty, format, new CompiledBinding(path), "queued");
        Assert.That(published, Is.EqualTo(new[] { "1 queued" }));
        Assert.That(format.ReadCount, Is.EqualTo(1));

        source.Add("two");
        Assert.That(ToolTip.GetTip(target), Is.EqualTo("2 queued"));
    }

    [AvaloniaTest]
    public void MultiStepCompiledArgument_SourceChanges_AppliesConverterThenStringFormat()
    {
        var source = new Border { Padding = new Thickness(3) };
        var target = new TextBlock { DataContext = source };
        var argument = CompiledBinding.Create((Border b) => b.Padding.Left,
            converter: new FuncValueConverter<double, double>(x => x * 2), stringFormat: "[{0} px]");
        using var binding = BindLocalized(target, TextBlock.TextProperty, "width: {0}", argument);
        Assert.That(target.Text, Is.EqualTo("width: [6 px]"));

        source.Padding = new Thickness(8);
        Assert.That(target.Text, Is.EqualTo("width: [16 px]"));
    }

    [AvaloniaTest]
    public void ReflectionArgument_PathFailsAndRecovers_UsesFallbackThenLiveValue()
    {
        var target = new TextBlock { DataContext = new object() };
        var argument = new Binding(nameof(TextBlock.Text)) { FallbackValue = "missing" };
        using var binding = BindLocalized(target, TextBlock.TextProperty, "result: {0}", argument);
        Assert.That(target.Text, Is.EqualTo("result: missing"));

        target.DataContext = new TextBlock { Text = "ready" };
        Assert.That(target.Text, Is.EqualTo("result: ready"));
        target.DataContext = new object();
        Assert.That(target.Text, Is.EqualTo("result: missing"));
    }

    [AvaloniaTest]
    public void EndpointHint_EndpointsChangeInSharedStyle_FormatsEachControlAndItsTemplate()
    {
        var styles = (Styles)AvaloniaXamlLoader.Load(new Uri("avares://Everywhere.Core/Assets/Styles.axaml"));
        var resources = (ResourceDictionary)AvaloniaXamlLoader.Load(
            new Uri("avares://Everywhere.Core/Views/Configuration/PreviewEndpointTextBox.axaml"));
        var theme = (ControlTheme?)resources[typeof(PreviewEndpointTextBox)];
        var first = new PreviewEndpointTextBox { Theme = theme, Endpoint = "https://first.example" };
        var second = new PreviewEndpointTextBox { Theme = theme, Endpoint = "https://second.example" };
        var window = new Window { Styles = { styles }, Content = new StackPanel { Children = { first, second } } };
        try
        {
            window.Show();
            AssertHints(first);
            AssertHints(second);
            second.Endpoint = "https://changed.example";
            AssertHints(second);
            AssertHints(first);
        }
        finally
        {
            window.Close();
        }

        static void AssertHints(PreviewEndpointTextBox control)
        {
            var expected = string.Format(LocaleKey.PreviewEndpointTextBox_Hint.I18N(), control.PreviewEndpoint);
            var editor = control.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.That(ControlAssist.GetHint(control), Is.EqualTo(expected), "Style setter on the control");
            Assert.That(ControlAssist.GetHint(editor), Is.EqualTo(expected), "Template binding on its editor");
        }
    }

    [AvaloniaTest]
    [TestCase(0)]
    [TestCase(23)]
    public void TerminalStatus_CompletedBlockIsShown_FormatsTooltipWithCompletionDetails(int exitCode)
    {
        // A restored history block has completion metadata but no live terminal session.
        var block = RestoreTerminalBlock(ShellType.PowerShell, "saved command", DateTimeOffset.UtcNow.AddSeconds(-32));
        block.Complete(exitCode);
        var terminal = new TerminalView { DisplayBlock = block };
        var window = new Window();
        var application = Application.Current ?? throw new AssertionException("Missing headless application.");
        var resources = new ResourceDictionary();
        foreach (var path in new[] { "Assets/Resources", "Views/Controls/ConditionalContentControl", "Views/Chat/TerminalView" })
        {
            resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri($"avares://Everywhere.Core/{path}.axaml")));
        }
        application.Resources.MergedDictionaries.Add(resources);
        try
        {
            window.Content = terminal;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tips = terminal.GetVisualDescendants().OfType<Border>().Select(ToolTip.GetTip).OfType<string>();
            var key = exitCode == 0 ? LocaleKey.TerminalView_SuccessToolTip_Tip : LocaleKey.TerminalView_ErrorToolTip_Tip;
            var started = block.CreatedAt.DateTime.ToLocalTime().ToString(CultureInfo.CurrentCulture);
            var seconds = block.Elapsed.TotalSeconds.ToString("N", CultureInfo.CurrentCulture);
            Assert.That(tips, Does.Contain(string.Format(key.I18N(), started, seconds, exitCode)));
        }
        finally
        {
            window.Close();
            application.Resources.MergedDictionaries.Remove(resources);
        }
    }

    [AvaloniaTest]
    public void MultiBinding_LocalizedChildUpdates_FormatsNewValue()
    {
        var source = new TextBlock { Text = "before" };
        var target = new Run { DataContext = source };
        var nested = Localize("value={0}", new Binding(nameof(TextBlock.Text)));
        using var binding = target.Bind(Run.TextProperty, new MultiBinding { Bindings = [nested], StringFormat = "[{0}]" });
        Assert.That(target.Text, Is.EqualTo("[value=before]"));

        source.Text = "after";
        Assert.That(target.Text, Is.EqualTo("[value=after]"));
    }

    [AvaloniaTest]
    public async Task LiveAndResolvedKeys_BackgroundLanguageChange_UpdatesOnlyLiveText()
    {
        var oldLocale = LocaleManager.CurrentLocale;
        try
        {
            LocaleManager.CurrentLocale = LocaleName.En;
            var label = new TextBlock();
            var hint = new TextBlock();
            var key = LocaleKey.Common_Retry;
            var snapshot = new I18NExtension(key) { Resolve = true }.ProvideValue(Substitute.For<IServiceProvider>());
            using var labelBinding = BindLocalized(label, TextBlock.TextProperty, key);
            using var hintBinding = BindLocalized(hint, TextBlock.TextProperty, LocaleKey.PreviewEndpointTextBox_Hint, key);
            var english = key.I18N();
            Assert.That(snapshot, Is.EqualTo(english));
            Assert.That(label.Text, Is.EqualTo(english));
            Assert.That(hint.Text, Is.EqualTo(string.Format(LocaleKey.PreviewEndpointTextBox_Hint.I18N(), english)));

            await Task.Run(() => LocaleManager.CurrentLocale = LocaleName.ZhHans).WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            var chinese = key.I18N();
            Assert.That(chinese, Is.Not.EqualTo(english));
            Assert.That(label.Text, Is.EqualTo(chinese));
            Assert.That(hint.Text, Is.EqualTo(string.Format(LocaleKey.PreviewEndpointTextBox_Hint.I18N(), chinese)));
            Assert.That(snapshot, Is.EqualTo(english));
        }
        finally
        {
            LocaleManager.CurrentLocale = oldLocale;
        }
    }

    [AvaloniaTest]
    public void ConditionalStyle_DeactivatesAndReactivates_ReleasesAndRestartsArgumentSubscriptions()
    {
        using var argument = new LiveArgument();
        var source = new TextBlock { Text = "first" };
        var target = new TextBlock { DataContext = source, Classes = { "localized" } };
        var style = new Style(x => x.OfType<TextBlock>().Class("localized"));
        style.Setters.Add(new Setter(ToolTip.TipProperty, Localize("{0}: {1}", new Binding(nameof(TextBlock.Text)), argument)));
        var window = new Window { Styles = { style }, Content = target };
        try
        {
            window.Show();
            Assert.That(ToolTip.GetTip(target), Is.EqualTo("first: pending"));
            var expression = BindingOperations.GetBindingExpressionBase(target, ToolTip.TipProperty)
                ?? throw new AssertionException("Missing active style binding.");
            argument.Values.OnNext("ready");
            Assert.That(ToolTip.GetTip(target), Is.EqualTo("first: ready"));

            target.Classes.Remove("localized");
            Assert.That(argument.Values.HasObservers, Is.False);
            Assert.That(WeakReferenceMessenger.Default.IsRegistered<LocaleChangedMessage>(expression), Is.False);
            source.Text = "second";
            argument.Values.OnNext("done");
            Assert.That(ToolTip.GetTip(target), Is.Null);

            target.Classes.Add("localized");
            Assert.That(ToolTip.GetTip(target), Is.EqualTo("second: done"));
            Assert.That(argument.Values.HasObservers, Is.True);
            source.Text = "third";
            Assert.That(ToolTip.GetTip(target), Is.EqualTo("third: done"));
        }
        finally
        {
            window.Close();
        }
    }

    private static BindingExpressionBase BindLocalized(AvaloniaObject target, AvaloniaProperty property, object key, params object[] args)
    {
        var provideTarget = Substitute.For<IProvideValueTarget>();
        provideTarget.TargetObject.Returns(target);
        provideTarget.TargetProperty.Returns(property);
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IProvideValueTarget)).Returns(provideTarget);
        var extension = new I18NExtension(key) { Arguments = [.. args] };
        return target.Bind(property, (BindingBase)extension.ProvideValue(services));
    }

    private static BindingBase Localize(object key, params object[] args)
    {
        var extension = new I18NExtension(key) { Arguments = [.. args] };
        return (BindingBase)extension.ProvideValue(Substitute.For<IServiceProvider>());
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ChatPluginTerminalDisplayBlock RestoreTerminalBlock(ShellType shell, string command, DateTimeOffset createdAt);

    private sealed class CountingFormat
    {
        public int ReadCount { get; private set; }

        public override string ToString()
        {
            ReadCount++;
            return "{0} {1}";
        }
    }

    private sealed class LiveArgument : IDynamicLocaleKey, IDisposable
    {
        public BehaviorSubject<string> Values { get; } = new("pending");

        public IDisposable Subscribe(IObserver<object?> observer) => Values.Subscribe(observer);
        public override string ToString() => Values.Value;
        public void Dispose() => Values.Dispose();
    }
}
