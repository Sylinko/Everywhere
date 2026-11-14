using System.Runtime.InteropServices;
using Avalonia;
using Everywhere.Automation;

namespace Everywhere.Linux.Automation;

/// <summary>
/// AT-SPI C ABI. Returned strings, rectangles and GErrors are explicitly freed.
/// </summary>
public static partial class AtspiNative
{
    private const string Library = "libatspi.so.0";

    /// <summary>
    /// Initializes the native accessibility client.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_init();

    /// <summary>
    /// Shuts down the native accessibility client.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_exit();

    /// <summary>
    /// Configures the native request and startup timeouts in milliseconds.
    /// </summary>
    [LibraryImport(Library)]
    public static partial void atspi_set_timeout(int milliseconds, int startupMilliseconds);

    /// <summary>
    /// Returns the native desktop at the requested index.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_get_desktop(int index);

    /// <summary>
    /// Clears cached observations for the native accessible object.
    /// </summary>
    [LibraryImport(Library)]
    public static partial void atspi_accessible_clear_cache(nint item);

    /// <summary>
    /// Returns the allocated UTF-8 name of an accessible object.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_name(nint item, out nint error);

    /// <summary>
    /// Returns the allocated UTF-8 role name of an accessible object.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_role_name(nint item, out nint error);

    /// <summary>
    /// Returns the number of native children.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_accessible_get_child_count(nint item, out nint error);

    /// <summary>
    /// Returns an owned reference to the indexed native child.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_child_at_index(nint item, int index, out nint error);

    /// <summary>
    /// Returns an owned reference to the native parent.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_parent(nint item, out nint error);

    /// <summary>
    /// Returns the accessible object's native sibling index.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_accessible_get_index_in_parent(nint item, out nint error);

    /// <summary>
    /// Returns the accessible provider's process identifier.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_accessible_get_process_id(nint item, out nint error);

    /// <summary>
    /// Returns an owned reference to the accessible object's state set.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_state_set(nint item);

    /// <summary>
    /// Returns an owned native attribute table.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_attributes(nint item, out nint error);

    /// <summary>
    /// Reports whether the native state set contains a state.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_state_set_contains(nint states, AtspiState state);

    /// <summary>
    /// Returns an owned native component interface when available.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_component_iface(nint item);

    /// <summary>
    /// Returns an owned native text interface when available.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_text_iface(nint item);

    /// <summary>
    /// Returns an owned native editable-text interface when available.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_editable_text_iface(nint item);

    /// <summary>
    /// Returns an owned native action interface when available.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_accessible_get_action_iface(nint item);

    /// <summary>
    /// Returns an allocated component rectangle in the requested coordinate space.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_component_get_extents(nint item, AtspiCoordinates coordinates, out nint error);

    /// <summary>
    /// Returns an owned reference to the accessible object at a component point.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_component_get_accessible_at_point(nint item, int x, int y, AtspiCoordinates coordinates, out nint error);

    /// <summary>
    /// Requests keyboard focus for the native component.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_component_grab_focus(nint item, out nint error);

    /// <summary>
    /// Returns the number of native Unicode characters in a text interface.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_text_get_character_count(nint item, out nint error);

    /// <summary>
    /// Returns an allocated UTF-8 string for the native character range.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_text_get_text(nint item, int start, int end, out nint error);

    /// <summary>
    /// Returns the number of selected native text ranges.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_text_get_n_selections(nint item, out nint error);

    /// <summary>
    /// Returns an allocated native text selection range.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint atspi_text_get_selection(nint item, int index, out nint error);

    /// <summary>
    /// Replaces the native editable text contents.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int atspi_editable_text_set_text_contents(nint item, string text, out nint error);

    /// <summary>
    /// Invokes the indexed native semantic action.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int atspi_action_do_action(nint item, int index, out nint error);

    /// <summary>
    /// Receives a native accessibility event and its listener user data.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(nint nativeEvent, nint userData);

    /// <summary>
    /// Creates a native listener that invokes the retained managed callback.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint atspi_event_listener_new(EventCallback callback, nint userData, nint destroy);

    /// <summary>
    /// Registers a native listener for an accessibility event type.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int atspi_event_listener_register(nint listener, string eventType, out nint error);

    /// <summary>
    /// Deregisters a native listener for an accessibility event type.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int atspi_event_listener_deregister(nint listener, string eventType, out nint error);

    /// <summary>
    /// Acquires another reference to a native GLib object.
    /// </summary>
    [LibraryImport("libgobject-2.0.so.0")]
    public static partial nint g_object_ref(nint item);

    /// <summary>
    /// Releases a reference to a native GLib object.
    /// </summary>
    [LibraryImport("libgobject-2.0.so.0")]
    public static partial void g_object_unref(nint item);

    /// <summary>
    /// Returns the GLib boxed type used by accessibility events.
    /// </summary>
    [LibraryImport(Library)]
    public static partial nuint atspi_event_get_type();

    /// <summary>
    /// Frees a GLib boxed value using its native type.
    /// </summary>
    [LibraryImport("libgobject-2.0.so.0")]
    public static partial void g_boxed_free(nuint type, nint item);

    /// <summary>
    /// Frees memory allocated by GLib.
    /// </summary>
    [LibraryImport("libglib-2.0.so.0")]
    public static partial void g_free(nint item);

    /// <summary>
    /// Frees a native GLib error.
    /// </summary>
    [LibraryImport("libglib-2.0.so.0")]
    public static partial void g_error_free(nint error);

    /// <summary>
    /// Releases a reference to a native GLib hash table.
    /// </summary>
    [LibraryImport("libglib-2.0.so.0")]
    public static partial void g_hash_table_unref(nint table);

    /// <summary>
    /// Processes one iteration of a native GLib main context.
    /// </summary>
    [LibraryImport("libglib-2.0.so.0")]
    public static partial int g_main_context_iteration(nint context, int shouldBlock);

    /// <summary>
    /// Returns a native result or throws after releasing its associated GLib error.
    /// </summary>
    public static T Checked<T>(T result, nint error)
    {
        if (error == 0)
        {
            return result;
        }

        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "Accessibility provider failure.";
        g_error_free(error);
        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            throw new TimeoutException(message);
        }

        throw new VisualElementProviderException(VisualElementQueryFailureKind.ProviderFailure, message);
    }

    /// <summary>
    /// Copies an allocated native UTF-8 result and releases its storage.
    /// </summary>
    public static string String(nint pointer, nint error)
    {
        try
        {
            Checked(0, error);
            return Marshal.PtrToStringUTF8(pointer) ?? "";
        }
        finally
        {
            if (pointer != 0)
            {
                g_free(pointer);
            }
        }
    }

    /// <summary>
    /// Reads the native child count and checks provider errors.
    /// </summary>
    public static int Count(nint item) => Checked(atspi_accessible_get_child_count(item, out var error), error);

    /// <summary>
    /// Acquires an indexed native child and checks provider errors.
    /// </summary>
    public static nint Child(nint item, int index) => Checked(atspi_accessible_get_child_at_index(item, index, out var error), error);

    /// <summary>
    /// Reads and releases the native accessible name.
    /// </summary>
    public static string Name(nint item) => String(atspi_accessible_get_name(item, out var error), error);

    /// <summary>
    /// Reads and releases the native accessible role name.
    /// </summary>
    public static string Role(nint item) => String(atspi_accessible_get_role_name(item, out var error), error);

    /// <summary>
    /// Reads the native provider process identifier and checks provider errors.
    /// </summary>
    public static int Pid(nint item) => Checked(atspi_accessible_get_process_id(item, out var error), error);

    /// <summary>
    /// Reads one native state while keeping the temporary state-set reference scoped.
    /// </summary>
    public static bool HasState(nint item, AtspiState state)
    {
        var states = atspi_accessible_get_state_set(item);
        if (states == 0)
        {
            return false;
        }

        try
        {
            return atspi_state_set_contains(states, state) != 0;
        }
        finally
        {
            g_object_unref(states);
        }
    }

    /// <summary>
    /// Releases a native accessibility event after its callback completes.
    /// </summary>
    public static void FreeEvent(nint nativeEvent) => g_boxed_free(atspi_event_get_type(), nativeEvent);

    /// <summary>
    /// An ATK plug can be exposed through a socket while its native parent index
    /// is detached. Avoid window-coordinate calls that reenter that bridge.
    /// </summary>
    public static bool IsDetachedEmbeddedRoot(nint item)
    {
        return Role(item) == "filler" && Checked(atspi_accessible_get_index_in_parent(item, out var error), error) < 0;
    }

    /// <summary>
    /// Reads a component rectangle and releases both temporary native objects.
    /// </summary>
    public static PixelRect? ReadExtents(nint accessible, AtspiCoordinates coordinates)
    {
        var component = atspi_accessible_get_component_iface(accessible);
        if (component == 0)
        {
            return null;
        }

        try
        {
            var pointer = Checked(atspi_component_get_extents(component, coordinates, out var error), error);
            if (pointer == 0)
            {
                return null;
            }

            try
            {
                var rectangle = Marshal.PtrToStructure<NativeRectangle>(pointer);
                if (rectangle.Width <= 0 || rectangle.Height <= 0 || rectangle.X == int.MinValue || rectangle.Y == int.MinValue)
                {
                    return null;
                }

                return new PixelRect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            }
            finally
            {
                g_free(pointer);
            }
        }
        finally
        {
            g_object_unref(component);
        }
    }

    // These fields preserve the sequential layout of the native AT-SPI rectangle.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }
}

/// <summary>
/// AT-SPI coordinate space; window coordinates work on native Wayland.
/// </summary>
public enum AtspiCoordinates
{
    Screen = 0,
    Window = 1,
    Parent = 2,
}

/// <summary>
/// Native AT-SPI state indices, rather than the shared visual-state bitmask.
/// </summary>
public enum AtspiState
{
    Defunct = 6,
    Editable = 7,
    Enabled = 8,
    Focused = 12,
    Selected = 23,
    Sensitive = 24,
    Showing = 25,
    Visible = 30,
    ManagesDescendants = 31,
    ReadOnly = 43,
}

/// <summary>
/// Bounds Linux native traversal independently of the shared snapshot/output policy.
/// Time is checked between calls; libatspi also has its own per-call timeout.
/// </summary>
internal static class AtspiQueryLimits
{
    public const int MaximumNodes = 256;
    public const int MaximumElapsedMilliseconds = 500;
}