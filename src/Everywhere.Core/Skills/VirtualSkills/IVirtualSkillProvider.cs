namespace Everywhere.Skills;

/// <summary>
/// Supplies read-only virtual skills that do not need to exist on the local file system.
/// </summary>
public interface IVirtualSkillProvider
{
    /// <summary>
    /// Enumerates the provider's current virtual skills.
    /// </summary>
    IAsyncEnumerable<VirtualSkill> ListAsync(CancellationToken cancellationToken = default);
}