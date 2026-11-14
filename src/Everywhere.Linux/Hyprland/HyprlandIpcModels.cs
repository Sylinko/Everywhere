using System.Globalization;
using System.Text.Json.Serialization;
using Avalonia;

namespace Everywhere.Linux.Hyprland;

/// <summary>
/// One output's physical mode and compositor logical placement.
/// </summary>
public sealed record HyprlandMonitor
{
    /// <summary>
    /// Gets the compositor-assigned identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// Gets the compositor-reported name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// Gets the logical horizontal placement of the output.
    /// </summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>
    /// Gets the logical vertical placement of the output.
    /// </summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>
    /// Gets the physical mode width in pixels.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>
    /// Gets the physical mode height in pixels.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>
    /// Gets the output pixel density relative to logical coordinates.
    /// </summary>
    [JsonPropertyName("scale")]
    public double Scale { get; init; } = 1;

    /// <summary>
    /// Gets the compositor output transform.
    /// </summary>
    [JsonPropertyName("transform")]
    public int Transform { get; init; }

    /// <summary>
    /// Gets whether the output currently has focus.
    /// </summary>
    [JsonPropertyName("focused")]
    public bool IsFocused { get; init; }

    /// <summary>
    /// Gets whether the output is disabled.
    /// </summary>
    [JsonPropertyName("disabled")]
    public bool IsDisabled { get; init; }

    /// <summary>
    /// Gets the mirrored output name, or none for an independent output.
    /// </summary>
    [JsonPropertyName("mirrorOf")]
    public string MirrorOf { get; init; } = "none";

    /// <summary>
    /// Gets the output's active workspace.
    /// </summary>
    [JsonPropertyName("activeWorkspace")]
    public HyprlandWorkspace ActiveWorkspace { get; init; } = new();

    /// <summary>
    /// Gets the output's visible special workspace.
    /// </summary>
    [JsonPropertyName("specialWorkspace")]
    public HyprlandWorkspace SpecialWorkspace { get; init; } = new();

    /// <summary>
    /// Bounds in compositor logical desktop coordinates; rotated modes exchange axes.
    /// </summary>
    [JsonIgnore]
    public PixelRect Bounds
    {
        get
        {
            return new(
                X,
                Y,
                (int)Math.Round((Transform % 2 == 0 ? Width : Height) / Scale),
                (int)Math.Round((Transform % 2 == 0 ? Height : Width) / Scale));
        }
    }
}

/// <summary>
/// A compositor workspace identity.
/// </summary>
public sealed record HyprlandWorkspace
{
    /// <summary>
    /// Gets the compositor-assigned identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// Gets the compositor-reported name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
}

/// <summary>
/// One compositor-managed application window, independent of its toolkit or display protocol.
/// </summary>
public sealed record HyprlandWindow
{
    /// <summary>
    /// Gets the opaque compositor window address.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    /// <summary>
    /// Gets the application process identifier.
    /// </summary>
    [JsonPropertyName("pid")]
    public int ProcessId { get; init; }

    /// <summary>
    /// Gets the compositor-reported application class.
    /// </summary>
    [JsonPropertyName("class")]
    public string ApplicationId { get; init; } = "";

    /// <summary>
    /// Gets the current window title.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    /// <summary>
    /// Gets the title observed when the window was created.
    /// </summary>
    [JsonPropertyName("initialTitle")]
    public string InitialTitle { get; init; } = "";

    /// <summary>
    /// Gets the client surface position in logical desktop coordinates.
    /// </summary>
    [JsonPropertyName("at")]
    public int[] Position { get; init; } = [];

    /// <summary>
    /// Gets the client surface dimensions in logical desktop units.
    /// </summary>
    [JsonPropertyName("size")]
    public int[] Size { get; init; } = [];

    /// <summary>
    /// Gets whether the application surface is mapped.
    /// </summary>
    [JsonPropertyName("mapped")]
    public bool IsMapped { get; init; }

    /// <summary>
    /// Gets whether the compositor has hidden the window.
    /// </summary>
    [JsonPropertyName("hidden")]
    public bool IsHidden { get; init; }

    /// <summary>
    /// Gets the identifier of the output containing the window.
    /// </summary>
    [JsonPropertyName("monitor")]
    public int MonitorId { get; init; }

    /// <summary>
    /// Gets whether the window uses floating placement.
    /// </summary>
    [JsonPropertyName("floating")]
    public bool IsFloating { get; init; }

    /// <summary>
    /// Gets the compositor fullscreen mode.
    /// </summary>
    [JsonPropertyName("fullscreen")]
    public int Fullscreen { get; init; }

    /// <summary>
    /// Gets the compositor focus-history rank.
    /// </summary>
    [JsonPropertyName("focusHistoryID")]
    public int FocusHistoryId { get; init; } = int.MaxValue;

    /// <summary>
    /// Gets the workspace containing the window.
    /// </summary>
    [JsonPropertyName("workspace")]
    public HyprlandWorkspace Workspace { get; init; } = new();

    /// <summary>
    /// Gets usable client bounds when the observation includes a complete position and size.
    /// </summary>
    [JsonIgnore]
    public PixelRect Bounds
    {
        get
        {
            return Position.Length == 2 && Size.Length == 2 ? new PixelRect(Position[0], Position[1], Size[0], Size[1]) : default;
        }
    }

    /// <summary>
    /// The compositor address is opaque; it must never be dereferenced as a native pointer.
    /// </summary>
    [JsonIgnore]
    public nint Handle => (nint)ulong.Parse(Address.AsSpan().TrimStart("0x"), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}