using Lucide.Avalonia;

namespace Everywhere.Chat;

/// <summary>
/// Describes a mutable runtime activity without exposing its presentation row.
/// Disposal completes the operation once; it does not destroy a retained visual row.
/// </summary>
public interface IBusyActivity : IDisposable
{
    /// <summary>Gets or sets the activity icon until the operation completes.</summary>
    LucideIconKind Icon { get; set; }

    /// <summary>Gets or sets the localized activity title until the operation completes.</summary>
    IDynamicLocaleKey HeaderKey { get; set; }

    /// <summary>Gets or sets an optional localized detail line until the operation completes.</summary>
    IDynamicLocaleKey? SecondaryHeaderKey { get; set; }

    /// <summary>Gets when the operation started.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Gets when the operation completed, or null while it is running.</summary>
    DateTimeOffset? FinishedAt { get; }

    /// <summary>Gets whether the operation can still receive updates.</summary>
    bool IsRunning { get; }
}
