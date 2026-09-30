using Everywhere.ProcessIsolation.Activation;

namespace Everywhere.Messages;

/// <summary>
/// Represents a message exchanged between parts of Main.
/// External activation uses a separate RPC contract.
/// </summary>
public abstract class ApplicationMessage;

/// <summary>
/// Message to show the main application window.
/// </summary>
/// <remarks>
/// When sending through WeakReferenceMessenger, use Send&lt;ApplicationMessage&gt; explicitly.
/// Receivers subscribe to ApplicationMessage; sending as ShowWindowMessage does not notify them.
/// For the main window, Route is forwarded after the window has been shown or activated.
/// </remarks>
/// <param name="name">
/// The name of the ViewModel to be shown.
/// </param>
public class ShowWindowMessage(string name, object? route = null) : ApplicationMessage
{
    public const string MainWindow = nameof(MainWindow);
    public const string ChatWindow = nameof(ChatWindow);

    public string Name { get; } = name;

    public object? Route { get; } = route;
}

/// <summary>
/// Message to handle when the application is launched via URL protocol.
/// </summary>
public class UrlProtocolCallbackMessage(string url) : ApplicationMessage
{
    public const string Scheme = UrlCallbackActivationRequest.UrlScheme;

    public string Url { get; } = url;
}