using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Markup.Xaml.Styling;
using Everywhere.AI;
using Everywhere.Cloud;
using Everywhere.Views;
using NSubstitute;

namespace Everywhere.Core.Tests.AI;

public class PresetModelDefinitionSelectorTests
{
    [AvaloniaTest]
    public void RefreshAndDetach_WhenSelectedModelDisappears_PreservesSavedSelectionAndUnsubscribes()
    {
        var configuration = new PresetAssistantConfiguration
        {
            ProviderId = "deepseek",
            ModelId = "selected",
            Name = "Selected",
            ContextLimit = 32000,
            OutputLimit = 4096,
            InputModalities = Modalities.Text,
            OutputModalities = Modalities.Text
        };
        var provider = Substitute.For<IPresetModelProvider>();
        var catalog = CreateCatalog("deepseek", [configuration.ToTemplate()]);
        provider.Catalog.Returns(_ => catalog);
        var assistantCatalog = CreateAssistantCatalog(provider);
        var selector = new PresetModelDefinitionSelector(provider, assistantCatalog, configuration);
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
            provider.CatalogChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
            Assert.That(selector.Items.Single(), Is.SameAs(originalItem));

            catalog = CreateCatalog("deepseek", []);
            provider.CatalogChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
            Assert.That(selector.IsUnlisted, Is.True);
            selector.SelectedItem = null;
            Assert.That(configuration.ModelId, Is.EqualTo("selected"));

            window.Content = null;
            catalog = CreateCatalog("deepseek", [configuration.ToTemplate()]);
            provider.CatalogChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
            Assert.That(selector.IsUnlisted, Is.True);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ProviderSelector_WhenAttachedOrRestored_DoesNotApplyModelUntilUserSelectsProvider()
    {
        var configuration = new PresetAssistantConfiguration
        {
            ProviderId = "deepseek",
            ModelId = "saved",
            Name = "Saved",
            ContextLimit = 32000,
            Schema = ModelProviderSchema.OpenAI
        };
        var provider = Substitute.For<IPresetModelProvider>();
        var newModel = new ModelDefinitionTemplate
        {
            ModelId = "new-default",
            Name = "New default",
            SupportsToolCall = true,
            InputModalities = Modalities.Text,
            OutputModalities = Modalities.Text,
            ContextLimit = 64000,
            OutputLimit = 4096,
            IsDefault = true
        };
        provider.Catalog.Returns(CreateCatalog("openai", [newModel]));
        provider.ClearReceivedCalls();
        var selector = new PresetModelProviderSelector(configuration, CreateAssistantCatalog(provider));
        var window = new Window { Content = selector, Width = 480, Height = 160 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.That(configuration.ModelId, Is.EqualTo("saved"));
            _ = provider.DidNotReceive().Catalog;

            configuration.ProviderId = "anthropic";
            Assert.That(configuration.ModelId, Is.EqualTo("saved"));
            _ = provider.DidNotReceive().Catalog;

            selector.SelectedItem = PresetModelTemplates.Providers.Single(template => template.Id == "openai");
            Assert.That(configuration.ProviderId, Is.EqualTo("openai"));
            Assert.That(configuration.ModelId, Is.EqualTo("new-default"));
            Assert.That(configuration.ContextLimit, Is.EqualTo(64000));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ProviderSelector_WhenSelectedProviderHasNoModels_ClearsPreviousModelData()
    {
        var configuration = new PresetAssistantConfiguration
        {
            ProviderId = "openai",
            ModelId = "old-model",
            ContextLimit = 32000,
            SupportsToolCall = true
        };
        var provider = Substitute.For<IPresetModelProvider>();
        provider.Catalog.Returns(CreateCatalog("deepseek", []));
        var selector = new PresetModelProviderSelector(configuration, CreateAssistantCatalog(provider));
        var window = new Window { Content = selector, Width = 480, Height = 160 };
        try
        {
            window.Show();
            selector.SelectedItem = PresetModelTemplates.Providers.Single(template => template.Id == "deepseek");
            Assert.That(configuration.ProviderId, Is.EqualTo("deepseek"));
            Assert.That(configuration.ModelId, Is.Null);
            Assert.That(configuration.ContextLimit, Is.Zero);
            Assert.That(configuration.SupportsToolCall, Is.False);
        }
        finally
        {
            window.Close();
        }
    }

    private static PresetModelCatalog CreateCatalog(
        string providerId,
        IReadOnlyList<ModelDefinitionTemplate> definitions)
    {
        var models = ModelCatalogSnapshot<ModelDefinitionTemplate, string>.Create(
            definitions.Select(static model => KeyValuePair.Create(model.ModelId, model)));
        return new PresetModelCatalog(
            [new PresetModelProviderCatalog(
                providerId,
                models,
                PresetModelSourceStatus.Available,
                definitions.Select(static model => model.ModelId).ToHashSet(StringComparer.Ordinal))],
            true);
    }

    private static AssistantCatalog CreateAssistantCatalog(IPresetModelProvider provider)
    {
        var official = Substitute.For<IOfficialModelProvider>();
        official.Catalog.Returns(OfficialModelCatalog.Empty);
        return new AssistantCatalog(provider, official);
    }
}
