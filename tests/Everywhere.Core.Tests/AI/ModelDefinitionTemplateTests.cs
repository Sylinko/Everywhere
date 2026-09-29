using System.Collections.Specialized;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using DynamicData;
using Everywhere.AI;
using Everywhere.Extensions;
using Everywhere.I18N;

namespace Everywhere.Core.Tests.AI;

public class ModelDefinitionTemplateTests
{
    [Test]
    public void Equality_WhenNestedValuesMatch_TreatsDefinitionsAsEqual()
    {
        var first = CreateModel(
            ["low", "high"],
            new JsonDynamicLocaleKey { ["en"] = "Description", ["zh-hans"] = "描述" });
        var second = CreateModel(
            ["low", "high"],
            new JsonDynamicLocaleKey { ["zh-hans"] = "描述", ["en"] = "Description" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(second.GetHashCode(), Is.EqualTo(first.GetHashCode()));
        }
    }

    [Test]
    public void Equality_WhenMetadataChanges_TreatsDefinitionsAsDifferentButIdsAsEqual()
    {
        var previous = CreateModel(["low", "high"], new DirectLocaleKey("Previous"));
        var current = previous with { ContextLimit = previous.ContextLimit * 2 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(current, Is.Not.EqualTo(previous));
            Assert.That(current.ModelId, Is.EqualTo(previous.ModelId));
        }
    }

    [AvaloniaTest]
    public void ComboBox_WhenItemsSourceIsReplaced_SelectsEqualNewInstance()
    {
        var previous = new SelectionItem("model", "Previous");
        var current = new SelectionItem("model", "Current");
        var comboBox = new ComboBox
        {
            ItemsSource = new[] { previous },
            SelectedItem = previous
        };
        var window = new Window { Content = comboBox };
        try
        {
            window.Show();
            comboBox.ItemsSource = new[] { current };

            Assert.That(comboBox.SelectedItem, Is.SameAs(current));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ComboBox_WhenSelectedItemRaisesCollectionReplace_ClearsSelection()
    {
        var previous = new SelectionItem("model", "Previous");
        var current = new SelectionItem("model", "Current");
        var items = new ObservableCollection<SelectionItem> { previous };
        var comboBox = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = previous
        };
        var window = new Window { Content = comboBox };
        try
        {
            window.Show();
            items[0] = current;

            Assert.That(comboBox.SelectedItem, Is.Null);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ComboBox_WhenDynamicDataBindingResets_SelectsEqualNewInstance()
    {
        var previous = new SelectionItem("model", "Previous");
        var current = new SelectionItem("model", "Current");
        using var source = new SourceList<SelectionItem>();
        source.Add(previous);
        var items = source.Connect().BindEx(out var itemsSubscription, resetThreshold: 0);
        using (itemsSubscription)
        {
            var collectionChangedActions = new List<NotifyCollectionChangedAction>();
            items.CollectionChanged += (_, args) => collectionChangedActions.Add(args.Action);
            var comboBox = new ComboBox
            {
                ItemsSource = items,
                SelectedItem = previous
            };
            var window = new Window { Content = comboBox };
            try
            {
                window.Show();
                source.Edit(list =>
                {
                    list.Clear();
                    list.Add(current);
                });

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(collectionChangedActions, Does.Contain(NotifyCollectionChangedAction.Reset));
                    Assert.That(comboBox.SelectedItem, Is.SameAs(current));
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static ModelDefinitionTemplate CreateModel(
        string[] reasoningEffortValues,
        IDynamicLocaleKey descriptionKey) =>
        new()
        {
            ModelId = "model",
            Name = "Model",
            SupportsToolCall = true,
            KnowledgeCutoff = new DateOnly(2025, 1, 1),
            ReleaseDate = new DateOnly(2025, 2, 1),
            DeprecationDate = new DateOnly(2027, 1, 1),
            InputModalities = Modalities.Text | Modalities.Image,
            OutputModalities = Modalities.Text,
            ContextLimit = 128000,
            OutputLimit = 16384,
            Specializations = ModelSpecializations.TitleGeneration,
            IconUrl = "https://example.test/model.png",
            DescriptionKey = descriptionKey,
            Pricing = new ModelPricing(
                [
                    new PricingTier(0, new TokenPricing(1, 2, 0.5)),
                    new PricingTier(200000, new TokenPricing(2, 4, 1))
                ],
                ModelPricingUnit.UsdPerMToken),
            IsQuotaLimited = true,
            ReasoningEffortValues = [.. reasoningEffortValues],
            IsDefault = true
        };

    private sealed record SelectionItem(string ModelId, string Label)
    {
        public bool Equals(SelectionItem? other) => other is not null && StringComparer.Ordinal.Equals(ModelId, other.ModelId);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ModelId);
    }
}
