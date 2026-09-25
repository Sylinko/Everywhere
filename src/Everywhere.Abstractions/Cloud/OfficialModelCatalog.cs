using Everywhere.AI;

namespace Everywhere.Cloud;

public sealed class OfficialModelCatalog(
    ModelCatalogSnapshot<OfficialModelDefinition, string> models,
    bool isAuthoritative
)
{
    public IReadOnlyList<OfficialModelDefinition> Definitions => Models.Definitions;

    public bool IsAuthoritative { get; } = isAuthoritative;

    public ModelCatalogSnapshot<OfficialModelDefinition, string> Models { get; } = models;

    public static OfficialModelCatalog Empty { get; } =
        new(ModelCatalogSnapshot<OfficialModelDefinition, string>.Empty, false);

    public OfficialModelDefinition? GetDefinition(string? modelId) =>
        modelId is not null && Models.TryGetDefinition(modelId, out var definition) ? definition : null;
}