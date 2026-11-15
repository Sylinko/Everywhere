using Avalonia.Data.Converters;
using Everywhere.AI;

namespace Everywhere.ValueConverters;

public static class ModalitiesConverters
{
    public static IValueConverter SupportsImage { get; } = new FuncValueConverter<Modalities, bool>(m => m.SupportsImage);

    public static IValueConverter SupportsPdf { get; } = new FuncValueConverter<Modalities, bool>(m => m.SupportsPdf);

    public static IValueConverter SupportsAudio { get; } = new FuncValueConverter<Modalities, bool>(m => m.SupportsAudio);

    public static IValueConverter SupportsVideo { get; } = new FuncValueConverter<Modalities, bool>(m => m.SupportsVideo);
}