using Everywhere.AI;

namespace Everywhere.Harness;

/// <summary>
/// Creates immutable <see cref="ModelSession"/> instances for configured assistants.
/// </summary>
public interface IModelSessionFactory
{
    /// <summary>
    /// Creates a new <see cref="ModelSession"/> snapshot for the given <paramref name="assistant"/>.
    /// </summary>
    ModelSession Create(Assistant assistant);
}