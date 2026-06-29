namespace Everywhere.Configuration.Engine;

/// <summary>
/// Describes how serious a settings engine diagnostic is.
/// </summary>
public enum SettingsEngineDiagnosticSeverity
{
    /// <summary>
    /// Informational diagnostic that does not indicate a failure.
    /// </summary>
    Info,

    /// <summary>
    /// Recoverable issue; the engine kept the existing runtime value or continued with a fallback.
    /// </summary>
    Warning,

    /// <summary>
    /// Error that prevented a read, migration, or write operation from completing normally.
    /// </summary>
    Error
}

/// <summary>
/// Categorizes diagnostics emitted by the settings engine.
/// </summary>
public enum SettingsEngineDiagnosticKind
{
    /// <summary>
    /// The settings JSON file could not be parsed.
    /// </summary>
    ParseFailure,

    /// <summary>
    /// A migration value could not be converted and an explicit default was used.
    /// </summary>
    MigrationFallback,

    /// <summary>
    /// A JSON value could not be converted to the target scalar type.
    /// </summary>
    ScalarConversionFailure,

    /// <summary>
    /// A terminal System.Text.Json subtree could not be deserialized.
    /// </summary>
    SerializedSubtreeFailure,

    /// <summary>
    /// A JSON member was not known to the descriptor under an error-reporting policy.
    /// </summary>
    UnknownMember,

    /// <summary>
    /// A settings shape is not supported by the current binder implementation.
    /// </summary>
    UnsupportedShape,

    /// <summary>
    /// The settings JSON document could not be written to disk.
    /// </summary>
    WriteFailure
}

/// <summary>
/// Represents one settings engine diagnostic.
/// </summary>
/// <param name="Kind">The diagnostic category.</param>
/// <param name="Severity">The diagnostic severity.</param>
/// <param name="Path">The settings or JSON path associated with the diagnostic.</param>
/// <param name="MessageKey">A localized, human-readable diagnostic message.</param>
/// <param name="Exception">The exception that caused the diagnostic, when available.</param>
public sealed record SettingsEngineDiagnostic(
    SettingsEngineDiagnosticKind Kind,
    SettingsEngineDiagnosticSeverity Severity,
    string Path,
    IDynamicLocaleKey MessageKey,
    Exception? Exception = null
);