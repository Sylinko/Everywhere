using Everywhere.Collections;

namespace Everywhere.Skills;

public interface ISkillManager
{
    IReadOnlyBindableList<SkillSourceGroup> SourceGroups { get; }

    SkillResolutionResult ResolveSkillReference(string reference);

    Task RefreshAsync(CancellationToken cancellationToken = default);
}
