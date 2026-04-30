using Bogus;

namespace Everywhere.VisualContext.Testing;

/// <summary>
/// Identifies the shape of random text requested by a scenario.
/// </summary>
public enum ScenarioTextKind
{
    Title,
    UserName,
    Sentence,
    Message,
    Paragraph,
}

/// <summary>
/// Generates multilingual scenario text through Bogus using a local deterministic randomizer.
/// </summary>
public static class ScenarioTextGenerator
{
    private static readonly string[] Locales = ["en", "zh_CN", "ja", "ar"];

    /// <summary>
    /// Generates text addressed by the context path, key, and requested text kind.
    /// </summary>
    public static string Generate(ScenarioContext context, string key, ScenarioTextKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var locale = Locales[context.RandomInt(key + "/locale", 0, Locales.Length)];
        var faker = new Faker(locale)
        {
            Random = new Randomizer(context.GetRandomizerSeed(key + "/faker")),
        };

        return kind switch
        {
            ScenarioTextKind.Title => faker.Lorem.Sentence(context.RandomInt(key + "/words", 2, 7)),
            ScenarioTextKind.UserName => faker.Name.FullName(),
            ScenarioTextKind.Sentence => faker.Lorem.Sentence(context.RandomInt(key + "/words", 5, 15)),
            ScenarioTextKind.Message => faker.Lorem.Sentence(context.RandomInt(key + "/words", 4, 18)),
            ScenarioTextKind.Paragraph => faker.Lorem.Paragraph(context.RandomInt(key + "/sentences", 3, 9)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
