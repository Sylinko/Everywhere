using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Everywhere.AI;
using Everywhere.AI.Configurator;
using Everywhere.Views;
using Everywhere.Configuration;
using Everywhere.Common;
using Everywhere.I18N;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Everywhere.Core.Tests.AI;

public class PresetModelDefinitionSelectorTests
{
    [AvaloniaTest]
    public void ProviderSelection_WhenChanged_KeepsDisplayedSelectionAndItemIdentity()
    {
        try { _ = LocaleManager.Shared; }
        catch (InvalidOperationException) { _ = new LocaleManager(); }
        var previousLocale = LocaleManager.CurrentLocale;
        var serviceField = typeof(ServiceLocator).GetField("_serviceProvider", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Service locator field missing.");
        var previousServices = serviceField.GetValue(null);
        using var services = new ServiceCollection().AddSingleton(Substitute.For<IPresetModelProvider>()).BuildServiceProvider();
        serviceField.SetValue(null, services);
        try
        {
            var assistant = new CustomAssistant { Configuration = new PresetAssistantConfiguration { ProviderId = "deepseek" } };
            var configurator = (PresetBasedAssistantConfigurator)assistant.Configurator;
            var item = new SettingsSelectionItem();
            // Reproduce the generated selection item's two-way binding and wrapper converter.
            item[!SettingsSelectionItem.ItemsSourceProperty] = new Binding(nameof(configurator.ModelProviderTemplates))
            {
                Source = configurator,
                Converter = new FuncValueConverter<IReadOnlyList<ModelProviderTemplate>, IEnumerable<SettingsSelectionItem.Item>>(
                    providers => providers?.Select(p => new SettingsSelectionItem.Item(new DirectLocaleKey(p), p, null)).ToArray() ?? [])
            };
            item[!SettingsItem.ValueProperty] = new Binding(nameof(configurator.ModelProviderTemplate)) { Source = configurator, Mode = BindingMode.TwoWay };
            var combo = new ComboBox();
            combo[!ItemsControl.ItemsSourceProperty] = new Binding(nameof(item.ItemsSource)) { Source = item };
            combo[!ComboBox.SelectedItemProperty] = new Binding(nameof(item.SelectedItem)) { Source = item, Mode = BindingMode.TwoWay };
            var originalItems = item.ItemsSource;
            var selected = originalItems.Single(i => i.Value is ModelProviderTemplate { Id: "google" });
            combo.SelectedItem = selected;
            Assert.That(((PresetAssistantConfiguration)assistant.Configuration).ProviderId, Is.EqualTo("google"));
            Assert.That(item.Value, Is.SameAs(selected.Value));
            Assert.That(combo.SelectedItem, Is.SameAs(selected));
            Assert.That(item.ItemsSource, Is.SameAs(originalItems));
            var name = new TextBlock();
            name[!TextBlock.TextProperty] = new Binding("ModelProviderTemplate.DisplayNameKey^") { Source = configurator };
            LocaleManager.CurrentLocale = LocaleName.ZhHans;
            Assert.That(name.Text, Is.EqualTo("谷歌（Gemini）"));
            LocaleManager.CurrentLocale = LocaleName.En;
            Assert.That(name.Text, Is.EqualTo("Google (Gemini)"));
            Assert.That(combo.SelectedItem, Is.SameAs(selected));
            Assert.That(item.ItemsSource, Is.SameAs(originalItems));
            assistant.Configuration = new PresetAssistantConfiguration { ProviderId = "deepseek" };
            Assert.That((combo.SelectedItem as SettingsSelectionItem.Item)?.Value, Is.EqualTo(configurator.ModelProviderTemplate));
            Assert.That(item.ItemsSource, Is.SameAs(originalItems));
        }
        finally
        {
            LocaleManager.CurrentLocale = previousLocale;
            serviceField.SetValue(null, previousServices);
        }
    }

    [AvaloniaTest]
    public void RefreshAndDetach_WhenSelectedModelDisappears_PreservesSelectionAndUnsubscribes()
    {
        var snapshot = new PresetAssistantConfiguration
        {
            ProviderId = "deepseek",
            ModelId = "selected", Name = "Selected", ContextLimit = 32000, OutputLimit = 4096,
            InputModalities = Modalities.Text, OutputModalities = Modalities.Text
        };
        var assistant = new CustomAssistant { Configuration = snapshot };
        var provider = Substitute.For<IPresetModelProvider>();
        provider.GetModelDefinitions("deepseek").Returns(new[] { snapshot.ToTemplate() });
        var selector = new PresetModelDefinitionSelector(provider, (PresetBasedAssistantConfigurator)assistant.Configurator);
        var window = new Window { Content = selector, Width = 480, Height = 160 };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Everywhere.Core/"))
        {
            Source = new Uri("avares://Everywhere.Core/Views/Configuration/PresetModelDefinitionSelector.axaml")
        });
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.That(selector.SelectedItem?.ModelId, Is.EqualTo("selected"));
            var originalItem = selector.Items.Single();
            provider.ModelsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
            Assert.That(selector.Items.Single(), Is.SameAs(originalItem));
            provider.GetModelDefinitions("deepseek").Returns(Array.Empty<ModelDefinitionTemplate>());
            provider.ModelsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
            Assert.That(selector.IsUnlisted, Is.True);
            Assert.That(assistant.Configuration, Is.SameAs(snapshot));
            selector.SelectedItem = null;
            Assert.That(assistant.Configuration.ModelId, Is.EqualTo("selected"));
            window.Content = null;
            provider.GetModelDefinitions("deepseek").Returns(new[] { snapshot.ToTemplate() });
            provider.ModelsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
            Assert.That(selector.IsUnlisted, Is.True);
        }
        finally { window.Close(); }
    }
}
