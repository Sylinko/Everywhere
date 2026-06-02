using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.I18N;

namespace Everywhere.Media;

public interface IMediaEngine<out TDescriptor>
{
    string Id { get; }

    TDescriptor Descriptor { get; }

    bool IsSupported { get; }

    IReadOnlyList<LocaleName> SupportedLocales { get; }

    IReadOnlyBindableList<DynamicNotification> Notifications { get; }
}
