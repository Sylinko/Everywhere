namespace Everywhere.Modding.Hosting.Installation;

/// <summary>
/// Reports startup source imports independently. Installations contains only successful,
/// unambiguous imports; it is not a desired-set replacement for an already running engine.
/// </summary>
public sealed record ModSourceImportResult
{
    /// <summary>
    /// Gets immutable installations ready for platform and compatibility selection.
    /// </summary>
    public required IReadOnlyList<ModInstallation> Installations { get; init; }

    /// <summary>
    /// Gets source failures without retaining original exception instances.
    /// </summary>
    public required IReadOnlyList<ModDiagnostic> Diagnostics { get; init; }
}