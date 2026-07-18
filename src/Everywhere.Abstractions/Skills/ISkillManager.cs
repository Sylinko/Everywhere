using System.ComponentModel;
using Everywhere.Collections;

namespace Everywhere.Skills;

public interface ISkillManager : INotifyPropertyChanged
{
    /// <summary>
    /// Gets whether a refresh is active or waiting to run.
    /// </summary>
    bool IsRefreshing { get; }

    IReadOnlyBindableList<SkillSourceGroup> SourceGroups { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}
