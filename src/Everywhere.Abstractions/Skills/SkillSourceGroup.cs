using Everywhere.Collections;

namespace Everywhere.Skills;

public sealed record SkillSourceGroup(
    SkillSourceRoot SourceRoot,
    string Name,
    string DirectoryPath,
    IReadOnlyBindableList<SkillDescriptor> Skills
);
