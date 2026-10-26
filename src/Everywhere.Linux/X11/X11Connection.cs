using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace Everywhere.Linux.X11;

/// <summary>
/// Owns one short-lived XCB connection. Replies and protocol errors are explicitly
/// owned, so a disappearing foreign window cannot install a process-wide Xlib
/// error handler or disturb Avalonia's separate connection.
/// </summary>
public sealed partial class X11Connection : IDisposable
{
    /// <summary>
    /// Gets the selected screen's root window identifier.
    /// </summary>
    public uint Root { get; }

    /// <summary>
    /// Gets the selected screen's bounds in root pixels.
    /// </summary>
    public PixelRect Bounds { get; }

    /// <summary>
    /// Gets the desktop output name used for the selected X screen.
    /// </summary>
    public string OutputName { get; }

    private readonly nint _connection;
    private readonly nint _setup;
    private readonly nint _screen;
    private readonly Dictionary<string, uint> _atoms = [];

    /// <summary>
    /// Opens an owned XCB connection to the selected display and screen.
    /// </summary>
    public X11Connection()
    {
        _connection = xcb_connect(0, out var screenNumber);
        if (_connection == 0 || xcb_connection_has_error(_connection) != 0)
        {
            if (_connection != 0)
            {
                xcb_disconnect(_connection);
            }
            throw new InvalidOperationException("The selected X11 display is unavailable.");
        }
        _setup = xcb_get_setup(_connection);
        var screens = xcb_setup_roots_iterator(_setup);
        for (var index = 0; index < screenNumber && screens.Remaining > 0; index++)
        {
            xcb_screen_next(ref screens);
        }
        if (screens.Remaining <= 0)
        {
            xcb_disconnect(_connection);
            throw new InvalidOperationException("The selected X11 screen is unavailable.");
        }
        _screen = screens.Data;
        Root = unchecked((uint)Marshal.ReadInt32(_screen));
        Bounds = new PixelRect(0, 0, (ushort)Marshal.ReadInt16(_screen, 20), (ushort)Marshal.ReadInt16(_screen, 22));
        OutputName = "X11-screen-" + screenNumber;
    }

    /// <summary>
    /// Reads bounded EWMH property bytes; XCB's wire CARDINALs remain 32-bit on LP64 systems.
    /// </summary>
    public byte[] ReadProperty(uint window, string name, byte expectedFormat)
    {
        var cookie = xcb_get_property(_connection, 0, window, Atom(name), 0, 0, 65536);
        using var reply = new NativeReply(xcb_get_property_reply(_connection, cookie, out var error), error);
        if (Marshal.ReadByte(reply.Pointer, 1) != expectedFormat)
        {
            return [];
        }
        var length = xcb_get_property_value_length(reply.Pointer);
        if (length is < 0 or > 262144)
        {
            throw new InvalidDataException("An X11 property exceeded its reply limit.");
        }
        var bytes = new byte[length];
        Marshal.Copy(xcb_get_property_value(reply.Pointer), bytes, 0, length);
        return bytes;
    }

    /// <summary>
    /// Reads 32-bit EWMH cardinal values from a window property.
    /// </summary>
    public uint[] ReadCardinals(uint window, string name)
    {
        var bytes = ReadProperty(window, name, 32);
        var values = new uint[bytes.Length / sizeof(uint)];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(uint));
        return values;
    }

    /// <summary>
    /// Reads a UTF-8 window property and removes its trailing null characters.
    /// </summary>
    public string ReadText(uint window, string name) => Encoding.UTF8.GetString(ReadProperty(window, name, 8)).TrimEnd('\0');

    /// <summary>
    /// Gets a cached atom identifier for a property name.
    /// </summary>
    public uint Atom(string name)
    {
        if (_atoms.TryGetValue(name, out var atom))
        {
            return atom;
        }
        using var reply = new NativeReply(
            xcb_intern_atom_reply(
                _connection,
                xcb_intern_atom(_connection, 0, (ushort)Encoding.UTF8.GetByteCount(name), name),
                out var error),
            error);
        atom = unchecked((uint)Marshal.ReadInt32(reply.Pointer, 8));
        _atoms.Add(name, atom);
        return atom;
    }

    /// <summary>
    /// Returns viewable client geometry translated to root coordinates.
    /// </summary>
    public PixelRect? GetClientBounds(uint window)
    {
        using var attributes = new NativeReply(
            xcb_get_window_attributes_reply(_connection, xcb_get_window_attributes(_connection, window), out var attributeError),
            attributeError);
        if (Marshal.ReadByte(attributes.Pointer, 26) != 2)
        {
            return null;
        }
        using var geometry = new NativeReply(
            xcb_get_geometry_reply(_connection, xcb_get_geometry(_connection, window), out var geometryError),
            geometryError);
        using var position = new NativeReply(
            xcb_translate_coordinates_reply(_connection, xcb_translate_coordinates(_connection, window, Root, 0, 0), out var positionError),
            positionError);
        return new PixelRect(
            Marshal.ReadInt16(position.Pointer, 8),
            Marshal.ReadInt16(position.Pointer, 10),
            (ushort)Marshal.ReadInt16(geometry.Pointer, 16),
            (ushort)Marshal.ReadInt16(geometry.Pointer, 18));
    }

    /// <summary>
    /// Gets the pointer position in root coordinates.
    /// </summary>
    public PixelPoint GetPointer()
    {
        using var reply = new NativeReply(xcb_query_pointer_reply(_connection, xcb_query_pointer(_connection, Root), out var error), error);
        return new PixelPoint(Marshal.ReadInt16(reply.Pointer, 16), Marshal.ReadInt16(reply.Pointer, 18));
    }

    /// <summary>
    /// Copies a standard little-endian TrueColor root image; other pixel layouts remain unsupported.
    /// </summary>
    public byte[] CaptureRoot()
    {
        var depth = Marshal.ReadByte(_screen, 38);
        var formats = xcb_setup_pixmap_formats_iterator(_setup);
        var hasSupportedFormat = false;
        while (formats.Remaining > 0)
        {
            if (Marshal.ReadByte(formats.Data) == depth && Marshal.ReadByte(formats.Data, 1) == 32)
            {
                hasSupportedFormat = true;
            }
            xcb_format_next(ref formats);
        }
        var rootVisual = Marshal.ReadInt32(_screen, 32);
        var depths = xcb_screen_allowed_depths_iterator(_screen);
        var hasSupportedVisual = false;
        while (depths.Remaining > 0)
        {
            var visuals = xcb_depth_visuals_iterator(depths.Data);
            while (visuals.Remaining > 0)
            {
                if (Marshal.ReadInt32(visuals.Data) == rootVisual && Marshal.ReadByte(visuals.Data, 4) == 4 &&
                    Marshal.ReadInt32(visuals.Data, 8) == 0xff0000 && Marshal.ReadInt32(visuals.Data, 12) == 0xff00 &&
                    Marshal.ReadInt32(visuals.Data, 16) == 0xff)
                {
                    hasSupportedVisual = true;
                }
                xcb_visualtype_next(ref visuals);
            }
            xcb_depth_next(ref depths);
        }
        var expectedLength = checked(Bounds.Width * Bounds.Height * 4);
        if (!hasSupportedFormat || !hasSupportedVisual || Marshal.ReadByte(_setup, 30) != 0 || expectedLength > 64 * 1024 * 1024)
        {
            throw new NotSupportedException("The X11 starter backend requires a bounded 32-bit little-endian TrueColor root image.");
        }
        using var reply = new NativeReply(
            xcb_get_image_reply(
                _connection,
                xcb_get_image(_connection, 2, Root, 0, 0, (ushort)Bounds.Width, (ushort)Bounds.Height, uint.MaxValue),
                out var error),
            error);
        if (xcb_get_image_data_length(reply.Pointer) != expectedLength)
        {
            throw new InvalidDataException("The X11 capture layout changed.");
        }
        var pixels = new byte[expectedLength];
        Marshal.Copy(xcb_get_image_data(reply.Pointer), pixels, 0, pixels.Length);
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 255;
        }
        return pixels;
    }

    /// <inheritdoc />
    public void Dispose() => xcb_disconnect(_connection);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIterator
    {
        public nint Data;
        public int Remaining;
        public int Index;
    }

    private sealed class NativeReply : IDisposable
    {
        public nint Pointer { get; }

        public NativeReply(nint pointer, nint error)
        {
            if (error != 0 || pointer == 0)
            {
                if (error != 0)
                {
                    free(error);
                }
                if (pointer != 0)
                {
                    free(pointer);
                }
                throw new InvalidOperationException("The X11 resource disappeared or rejected the request.");
            }
            Pointer = pointer;
        }

        /// <inheritdoc />
        public void Dispose() => free(Pointer);
    }

    [LibraryImport("libc.so.6")]
    private static partial void free(nint pointer);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_connect(nint displayName, out int screenNumber);

    [LibraryImport("libxcb.so.1")]
    private static partial void xcb_disconnect(nint connection);

    [LibraryImport("libxcb.so.1")]
    private static partial int xcb_connection_has_error(nint connection);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_get_setup(nint connection);

    [LibraryImport("libxcb.so.1")]
    private static partial NativeIterator xcb_setup_roots_iterator(nint setup);

    [LibraryImport("libxcb.so.1")]
    private static partial void xcb_screen_next(ref NativeIterator iterator);

    [LibraryImport("libxcb.so.1")]
    private static partial NativeIterator xcb_setup_pixmap_formats_iterator(nint setup);

    [LibraryImport("libxcb.so.1")]
    private static partial void xcb_format_next(ref NativeIterator iterator);

    [LibraryImport("libxcb.so.1")]
    private static partial NativeIterator xcb_screen_allowed_depths_iterator(nint screen);

    [LibraryImport("libxcb.so.1")]
    private static partial void xcb_depth_next(ref NativeIterator iterator);

    [LibraryImport("libxcb.so.1")]
    private static partial NativeIterator xcb_depth_visuals_iterator(nint depth);

    [LibraryImport("libxcb.so.1")]
    private static partial void xcb_visualtype_next(ref NativeIterator iterator);

    [LibraryImport("libxcb.so.1", StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint xcb_intern_atom(nint connection, byte onlyIfExists, ushort nameLength, string name);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_intern_atom_reply(nint connection, uint cookie, out nint error);

    [LibraryImport("libxcb.so.1")]
    private static partial uint xcb_get_property(nint connection, byte delete, uint window, uint property, uint type, uint offset, uint length);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_get_property_reply(nint connection, uint cookie, out nint error);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_get_property_value(nint reply);

    [LibraryImport("libxcb.so.1")]
    private static partial int xcb_get_property_value_length(nint reply);

    [LibraryImport("libxcb.so.1")]
    private static partial uint xcb_get_window_attributes(nint connection, uint window);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_get_window_attributes_reply(nint connection, uint cookie, out nint error);

    [LibraryImport("libxcb.so.1")]
    private static partial uint xcb_get_geometry(nint connection, uint drawable);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_get_geometry_reply(nint connection, uint cookie, out nint error);

    [LibraryImport("libxcb.so.1")]
    private static partial uint xcb_translate_coordinates(nint connection, uint source, uint destination, short x, short y);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_translate_coordinates_reply(nint connection, uint cookie, out nint error);

    [LibraryImport("libxcb.so.1")]
    private static partial uint xcb_query_pointer(nint connection, uint window);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_query_pointer_reply(nint connection, uint cookie, out nint error);

    [LibraryImport("libxcb.so.1")]
    private static partial uint xcb_get_image(nint connection, byte format, uint drawable, short x, short y, ushort width, ushort height, uint mask);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_get_image_reply(nint connection, uint cookie, out nint error);

    [LibraryImport("libxcb.so.1")]
    private static partial int xcb_get_image_data_length(nint reply);

    [LibraryImport("libxcb.so.1")]
    private static partial nint xcb_get_image_data(nint reply);
}