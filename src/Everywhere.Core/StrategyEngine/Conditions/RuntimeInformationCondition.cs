namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Condition that matches based on the runtime information (OS and architecture).
/// </summary>
public sealed class RuntimeInformationCondition : IStrategyCondition
{
    /// <summary>
    /// The operating system to match. If null, matches any OS.
    /// </summary>
    public OSPlatform? OS { get; init; }

    /// <summary>
    /// The architecture to match. If null, matches any architecture.
    /// </summary>
    public Architecture? Architecture { get; init; }

    public bool Evaluate(StrategyContext context)
    {
        if (OS.HasValue && !RuntimeInformation.IsOSPlatform(OS.Value))
        {
            return false;
        }

        if (Architecture.HasValue && RuntimeInformation.OSArchitecture != Architecture.Value)
        {
            return false;
        }

        return true;
    }
}