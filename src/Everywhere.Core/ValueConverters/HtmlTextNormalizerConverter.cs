using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

/// <summary>
///     Converts plain text to HTML by wrapping it in a &lt;body&gt; tag and replacing line breaks with &lt;br/&gt; tags.
///     If the input text already appears to be HTML (starting with &lt;html&gt;, &lt;body&gt;, or &lt;!DOCTYPE), it is returned unchanged.
/// </summary>
public sealed class HtmlTextNormalizerConverter : IValueConverter
{
    public static HtmlTextNormalizerConverter Shared { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
            return value;

        var span = text.AsSpan().TrimStart();

        if (span.StartsWith("<html>", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("<body>", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var isHtmlFragment = span.IndexOf("<p>", StringComparison.OrdinalIgnoreCase) >= 0 ||
            span.IndexOf("<div", StringComparison.OrdinalIgnoreCase) >= 0 ||
            span.IndexOf("<h", StringComparison.OrdinalIgnoreCase) >= 0;

        var estimatedCapacity = text.Length + 13 + (isHtmlFragment ? 0 : 32);
        var sb = new StringBuilder(estimatedCapacity);

        sb.Append("<body>");

        if (isHtmlFragment)
        {
            sb.Append(text);
        }
        else
        {
            var firstLine = true;
            foreach (var line in span.EnumerateLines())
            {
                if (!firstLine) sb.Append("<br/>");
                sb.Append(line);
                firstLine = false;
            }
        }

        sb.Append("</body>");
        return sb.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}