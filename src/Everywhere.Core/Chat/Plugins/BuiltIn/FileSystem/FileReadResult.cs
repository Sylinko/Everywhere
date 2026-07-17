namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Contains a bounded sequence of logical file units and format-specific metadata.
/// </summary>
public sealed record FileReadResult
{
    public required IReadOnlyList<Item> Items { get; init; }

    public required int Offset { get; init; }

    public required string Unit { get; init; }

    public int? Total { get; init; }

    public required bool HasMore { get; init; }

    public int NextOffset => Offset + Items.Sum(static item => item.UnitCount);

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// Represents one logical line or binary block returned by a handler.
    /// </summary>
    public sealed record Item(string Content, int UnitCount = 1, int? PageNumber = null);
}