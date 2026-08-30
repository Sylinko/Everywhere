namespace Everywhere.Automation.Testing;

/// <summary>
/// Defines a named declarative visual scenario.
/// </summary>
public sealed class Scenario
{
    /// <summary>
    /// Gets the stable scenario name used in reproduction output and random paths.
    /// </summary>
    public string Name { get; }

    private readonly Func<ScenarioContext, IReadOnlyList<VisualControl>> _factory;

    private Scenario(string name, Func<ScenarioContext, IReadOnlyList<VisualControl>> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        _factory = factory;
    }

    /// <summary>
    /// Defines a scenario from a compact declarative control factory.
    /// </summary>
    public static Scenario Define(string name, Func<ScenarioContext, VisualControl> factory) =>
        new(name, context => [factory(context)]);

    /// <summary>
    /// Defines a scenario containing genuinely disconnected root controls.
    /// </summary>
    public static Scenario DefineRoots(string name, Func<ScenarioContext, IReadOnlyList<VisualControl>> factory) =>
        new(name, factory);

    internal IReadOnlyList<VisualControl> Generate(ScenarioContext context) => _factory(context);
}

/// <summary>
/// Contains one generated scenario together with the name and seed required to reproduce it in the same revision.
/// </summary>
public sealed record GeneratedVisualScenario(string Name, long Seed, IReadOnlyList<VisualControl> Roots)
{
    /// <summary>
    /// Gets the only root in a single-root scenario.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scenario has zero or multiple roots.</exception>
    public VisualControl Root => Roots.Count == 1
        ? Roots[0]
        : throw new InvalidOperationException($"Scenario '{Name}' contains {Roots.Count} roots.");
}

/// <summary>
/// Materializes the root declaration for a named scenario and seed.
/// </summary>
public sealed class VisualScenarioGenerator
{
    /// <summary>
    /// Generates a declarative visual tree for the specified scenario and seed.
    /// </summary>
    public GeneratedVisualScenario Generate(Scenario scenario, long seed)
    {
        var context = new ScenarioContext(seed, scenario.Name);
        return new GeneratedVisualScenario(scenario.Name, seed, scenario.Generate(context));
    }
}
