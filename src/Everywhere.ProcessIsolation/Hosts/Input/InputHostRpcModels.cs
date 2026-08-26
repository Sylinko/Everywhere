using MessagePack;

namespace Everywhere.ProcessIsolation.Hosts.Input;

/// <summary>One keyboard shortcut desired by Main for the current Input connection.</summary>
[MessagePackObject]
public sealed partial class InputKeyboardRegistration
{
    /// <summary>Main-owned identity used by trigger notifications.</summary>
    [Key(0)]
    public required ulong RegistrationId { get; init; }

    /// <summary>Application key code interpreted by the platform Input session.</summary>
    [Key(1)]
    public required int Key { get; init; }

    /// <summary>Bitwise application modifier value interpreted by the platform Input session.</summary>
    [Key(2)]
    public required int Modifiers { get; init; }
}

/// <summary>One mouse shortcut desired by Main for the current Input connection.</summary>
[MessagePackObject]
public sealed partial class InputMouseRegistration
{
    /// <summary>Main-owned identity used by trigger notifications.</summary>
    [Key(0)]
    public required ulong RegistrationId { get; init; }

    /// <summary>Application mouse-button code interpreted by the platform Input session.</summary>
    [Key(1)]
    public required int Button { get; init; }

    /// <summary>Required press duration in <see cref="TimeSpan.Ticks"/>.</summary>
    [Key(2)]
    public required long DelayTicks { get; init; }
}

/// <summary>
/// Complete desired state for one Input connection. Applying a snapshot replaces
/// every prior registration and capture mode owned by that connection.
/// </summary>
[MessagePackObject]
public sealed partial class ApplyInputStateRequest
{
    /// <summary>Complete keyboard-registration set.</summary>
    [Key(0)]
    public required InputKeyboardRegistration[] KeyboardRegistrations { get; init; }

    /// <summary>Complete mouse-registration set.</summary>
    [Key(1)]
    public required InputMouseRegistration[] MouseRegistrations { get; init; }

    /// <summary>Main-owned capture identity, or zero when capture mode is inactive.</summary>
    [Key(2)]
    public required ulong CaptureId { get; init; }
}

/// <summary>Acknowledges that Input Host replaced its connection-owned desired state.</summary>
[MessagePackObject]
public sealed partial class ApplyInputStateResponse
{
    /// <summary>Whether the complete snapshot is now active.</summary>
    [Key(0)]
    public required bool IsApplied { get; init; }
}

/// <summary>Base timing and ordering data carried by every Input notification.</summary>
[MessagePackObject]
public sealed partial class ShortcutTriggeredNotification
{
    /// <summary>Main-owned registration identity from the active snapshot.</summary>
    [Key(0)]
    public required ulong RegistrationId { get; init; }

    /// <summary>Connection-local sequence shared by every Input notification kind.</summary>
    [Key(1)]
    public required ulong Sequence { get; init; }

    /// <summary><see cref="DateTimeOffset.UtcNow"/> ticks captured by Input Host.</summary>
    [Key(2)]
    public required long UtcTicks { get; init; }
}

/// <summary>Current shortcut observed while Main's capture scope is active.</summary>
[MessagePackObject]
public sealed partial class ShortcutCaptureChangedNotification
{
    /// <summary>Main-owned capture identity from the active snapshot.</summary>
    [Key(0)]
    public required ulong CaptureId { get; init; }

    /// <summary>Connection-local sequence shared by every Input notification kind.</summary>
    [Key(1)]
    public required ulong Sequence { get; init; }

    /// <summary><see cref="DateTimeOffset.UtcNow"/> ticks captured by Input Host.</summary>
    [Key(2)]
    public required long UtcTicks { get; init; }

    /// <summary>Application key code currently held by the user.</summary>
    [Key(3)]
    public required int Key { get; init; }

    /// <summary>Bitwise application modifier value currently held by the user.</summary>
    [Key(4)]
    public required int Modifiers { get; init; }
}

/// <summary>Final shortcut observed when Main's capture scope completes.</summary>
[MessagePackObject]
public sealed partial class ShortcutCaptureFinishedNotification
{
    /// <summary>Main-owned capture identity from the active snapshot.</summary>
    [Key(0)]
    public required ulong CaptureId { get; init; }

    /// <summary>Connection-local sequence shared by every Input notification kind.</summary>
    [Key(1)]
    public required ulong Sequence { get; init; }

    /// <summary><see cref="DateTimeOffset.UtcNow"/> ticks captured by Input Host.</summary>
    [Key(2)]
    public required long UtcTicks { get; init; }

    /// <summary>Final application key code, or zero when capture was cancelled.</summary>
    [Key(3)]
    public required int Key { get; init; }

    /// <summary>Final application modifier value, or zero when capture was cancelled.</summary>
    [Key(4)]
    public required int Modifiers { get; init; }
}