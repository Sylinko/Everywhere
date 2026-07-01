using MessagePack;

namespace Everywhere.Messages;

/// <summary>
/// Represents an application message. It can be sent between different parts of the application or different processes.
/// </summary>
[MessagePackObject]
[Union(0, typeof(ShowWindowMessage))]
[Union(1, typeof(UrlProtocolCallbackMessage))]
public abstract partial class ApplicationMessage;

/// <summary>
/// Message to show the main application window.
/// </summary>
/// <param name="name">
/// The name of the ViewModel to be shown.
/// </param>
[MessagePackObject]
public partial class ShowWindowMessage(string name, object? route = null) : ApplicationMessage
{
    public const string MainWindow = nameof(MainWindow);
    public const string ChatWindow = nameof(ChatWindow);

    [Key(0)]
    public string Name { get; } = name;

    [Key(1)]
    public object? Route { get; } = route;
}

/// <summary>
/// Message to handle when the application is launched via URL protocol.
/// </summary>
[MessagePackObject]
public partial class UrlProtocolCallbackMessage(string url) : ApplicationMessage
{
    public const string Scheme = "sylinko-everywhere";

    [Key(0)]
    public string Url { get; } = url;
}
