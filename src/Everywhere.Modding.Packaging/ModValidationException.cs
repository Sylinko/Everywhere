namespace Everywhere.Modding.Packaging;

/// <summary>
/// Reports invalid external package or selection data before activation begins.
/// </summary>
public sealed class ModValidationException(string code, string message) : Exception(message)
{
    /// <summary>
    /// Gets a stable diagnostic code for application localization and structured tool output.
    /// </summary>
    public string Code { get; } = code;
}