using System.ComponentModel;
using System.Globalization;

namespace Everywhere.Configuration;

/// <summary>
/// A TypeConverter for Guid that falls back to Guid.Empty if conversion fails.
/// </summary>
public sealed class FallbackGuidConverter : GuidConverter
{
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        return value is string g && Guid.TryParse(g, out var guid) ? guid : Guid.Empty;
    }
}