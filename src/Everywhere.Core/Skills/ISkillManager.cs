using Everywhere.Collections;

namespace Everywhere.Skills;

public interface ISkillManager
{
    IReadOnlyBindableList<SkillSourceGroup> SourceGroups { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}