using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

public static class UrlConverters
{
    /// <summary>
    /// Represents a value converter that extracts the host part from a given URL.
    /// This property is of type IValueConverter and is used to convert an input, which is expected to be a valid URL,
    /// into its host component. If the input is not a valid URL or does not contain a host, it returns null.
    /// </summary>
    public static IValueConverter ToHost { get; } = new FuncValueConverter<object?, string?>(x =>
        x is not Uri uri && !Uri.TryCreate(x?.ToString(), UriKind.Absolute, out uri!) ? null : uri.Host);
}
