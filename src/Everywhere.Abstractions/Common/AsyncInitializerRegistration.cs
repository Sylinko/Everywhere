namespace Everywhere.Common;

/// <summary>
/// Describes a startup step without constructing its service. The delegate resolves the
/// existing DI registration only when its ordered group is ready to run.
/// </summary>
public sealed record AsyncInitializerRegistration(
    AsyncInitializerIndex Index,
    Func<IServiceProvider, CancellationToken, Task> InitializeAsync
);