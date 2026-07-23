using Avalonia.Controls.Primitives;
using Everywhere.Configuration;

namespace Everywhere.Views;

/// <summary>
/// Edits the file-system plugin's path-scoped automatic approval globs.
/// </summary>
public sealed class FileSystemSettingsControl(FileSystemSettings settings) : TemplatedControl
{
    public FileSystemSettings Settings { get; } = settings;
}