namespace Everywhere.AI;

/// <summary>
/// Orders catalog models from newest to oldest and provides deterministic ordering when release
/// dates are equal or unavailable.
/// </summary>
public sealed class ModelDefinitionComparer : IComparer<ModelDefinitionTemplate>
{
    public static ModelDefinitionComparer Shared { get; } = new();

    public int Compare(ModelDefinitionTemplate? x, ModelDefinitionTemplate? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var byReleaseDate = Nullable.Compare(y.ReleaseDate, x.ReleaseDate);
        if (byReleaseDate != 0) return byReleaseDate;

        var byName = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(x.ModelId, y.ModelId);
    }
}