using Everywhere.AI;

namespace Everywhere.Core.Tests.AI;

public class ModelCatalogSnapshotTests
{
    [Test]
    public void PublishedCollections_DoNotExposeMutableSourceContainers()
    {
        var definition = new ModelDefinitionTemplate
        {
            ModelId = "model",
            Name = "Model",
            SupportsToolCall = false,
            InputModalities = Modalities.Text,
            OutputModalities = Modalities.Text,
            ContextLimit = 1,
            OutputLimit = 1
        };
        var snapshot = ModelCatalogSnapshot<ModelDefinitionTemplate, string>.Create(
            [KeyValuePair.Create(definition.ModelId, definition)]);
        var provider = new PresetModelProviderCatalog(
            "provider",
            snapshot,
            PresetModelSourceStatus.Available,
            new HashSet<string>([definition.ModelId], StringComparer.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Definitions, Is.Not.InstanceOf<ModelDefinitionTemplate[]>());
            Assert.That(provider.SourceModelIds, Is.Not.InstanceOf<HashSet<string>>());
        }
    }
}
