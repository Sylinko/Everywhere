using Avalonia.Data.Converters;
using Avalonia.Media;
using Everywhere.Common;

namespace Everywhere.ValueConverters;

/// <summary>
/// Provides value converters for converting between <see cref="SerializableColor"/> and <see cref="Color"/>.
/// </summary>
public static class SerializableColorConverters
{
    /// <summary>
    /// Converts a <see cref="SerializableColor"/> to a <see cref="Color"/> and vice versa.
    /// </summary>
    public static IValueConverter ToColor { get; } = new FuncValueConverter<SerializableColor?, Color?>(
        convert: x => x,
        convertBack: x => x);

    /// <summary>
    /// Converts a <see cref="Color"/> to a <see cref="SerializableColor"/> and vice versa.
    /// </summary>
    public static IValueConverter FromColor { get; } = new FuncValueConverter<Color?, SerializableColor?>(
        convert: x => x,
        convertBack: x => x);
}