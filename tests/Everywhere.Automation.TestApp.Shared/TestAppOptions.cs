using System.Globalization;
using Everywhere.Automation.Testing;

namespace Everywhere.Automation.TestApp;

/// <summary>
/// Contains the deterministic scenario selection passed to a controlled visual-context TestApp.
/// </summary>
public sealed record TestAppOptions(string Scenario, long Seed)
{
    /// <summary>
    /// Parses the required <c>--scenario</c> and <c>--seed</c> command-line arguments.
    /// </summary>
    public static TestAppOptions Parse(params IReadOnlyList<string> arguments)
    {
        string? scenario = null;
        long? seed = null;

        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--scenario" when i + 1 < arguments.Count:
                    scenario = arguments[++i];
                    break;
                case "--seed" when i + 1 < arguments.Count:
                    seed = long.Parse(arguments[++i], CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete TestApp argument '{arguments[i]}'.", nameof(arguments));
            }
        }

        if (string.IsNullOrWhiteSpace(scenario) || seed is null)
        {
            throw new ArgumentException("TestApps require --scenario <name> and --seed <number>.", nameof(arguments));
        }

        return new TestAppOptions(scenario, seed.Value);
    }

    /// <summary>
    /// Resolves the selected scenario from the shared common and extreme catalogs.
    /// </summary>
    public Scenario ResolveScenario()
    {
        foreach (var candidate in CommonScenarios.All)
        {
            if (string.Equals(candidate.Name, Scenario, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        foreach (var candidate in ExtremeScenarios.All)
        {
            if (string.Equals(candidate.Name, Scenario, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"Unknown visual scenario '{Scenario}'.", nameof(Scenario));
    }
}
