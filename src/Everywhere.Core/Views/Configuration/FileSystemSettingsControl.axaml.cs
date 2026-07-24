using Avalonia.Controls.Primitives;
using Everywhere.Configuration;

namespace Everywhere.Views;

/// <summary>
/// Edits the file-system plugin's path-scoped approval-bypass globs.
/// </summary>
public sealed class FileSystemSettingsControl(FileSystemSettings settings) : TemplatedControl
{
    public FileSystemSettings Settings { get; } = settings;
}