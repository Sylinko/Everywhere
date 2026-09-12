using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Views;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Everywhere.Configuration;

/// <summary>
/// Stores path-scoped approval-bypass rules for the file-system plugin.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class FileSystemSettings : ObservableObject
{
    private readonly FileSystemApprovalPathCollection _approvalPaths = [];
    private FileSystemApprovalGlob[] _compiledApprovalPaths = [];

    public FileSystemSettings()
    {
        _approvalPaths.ContentsChanged += HandleApprovalPathsChanged;
        RebuildCompiledApprovalPaths();
    }

    /// <summary>
    /// Gets or replaces the path approval collection while preserving the collection instance used by the UI.
    /// </summary>
    [SettingsSerializedSubtree]
    [SettingsItemIgnore]
    public FileSystemApprovalPathCollection ApprovalPaths
    {
        get => _approvalPaths;
        set
        {
            if (ReferenceEquals(value, _approvalPaths)) return;

            _approvalPaths.ReplaceWith(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the settings editor shown on the file-system plugin settings tab.
    /// </summary>
    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.FileSystemSettings_ApprovalPaths_Header, LocaleKey.FileSystemSettings_ApprovalPaths_Description)]
    [SettingsItem(Classes = ["FullWidth"])]
    public SettingsControl<FileSystemSettingsControl> ApprovalPathsControl => new(_ => new FileSystemSettingsControl(this));

    /// <summary>
    /// Returns whether every supplied path is covered by at least one configured approval glob.
    /// </summary>
    public bool ArePathsApproved(IEnumerable<string> paths)
    {
        var compiledPaths = Volatile.Read(ref _compiledApprovalPaths);
        if (compiledPaths.Length == 0) return false;

        var candidates = new List<string>();
        foreach (var path in paths.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!FileSystemApprovalGlob.TryNormalizeCandidatePath(path, out var candidate)) return false;
            candidates.Add(candidate);
        }

        return candidates.Count > 0 &&
            candidates.AsValueEnumerable().All(path => compiledPaths.AsValueEnumerable().Any(rule => rule.IsNormalizedPathMatch(path)));
    }

    /// <summary>
    /// Adds a normalized approval glob unless an equivalent rule already exists.
    /// </summary>
    public bool AddApprovalPath(string pattern) => _approvalPaths.AddPattern(pattern);

    [RelayCommand]
    private void AddEmptyApprovalPath(int? index)
    {
        if (index is null || index < 0 || index >= _approvalPaths.Count)
        {
            _approvalPaths.Add(new FileSystemApprovalPath());
        }
        else
        {
            _approvalPaths.Insert(index.Value + 1, new FileSystemApprovalPath());
        }
    }

    [RelayCommand]
    private void RemoveApprovalPath(int index) => _approvalPaths.SafeRemoveAt(index);

    private void HandleApprovalPathsChanged(object? sender, EventArgs e)
    {
        RebuildCompiledApprovalPaths();
        OnPropertyChanged(nameof(ApprovalPaths));
    }

    private void RebuildCompiledApprovalPaths()
    {
        var compiled = _approvalPaths
            .AsValueEnumerable()
            .Select(static item => FileSystemApprovalGlob.TryCreate(item.Pattern, out var glob) ? glob : null)
            .Where(static glob => glob is not null)
            .Cast<FileSystemApprovalGlob>()
            .ToArray();
        Volatile.Write(ref _compiledApprovalPaths, compiled);
    }
}

/// <summary>
/// Represents one user-editable file-system approval glob.
/// </summary>
public sealed class FileSystemApprovalPath : ObservableValidator
{
    [CustomValidation(typeof(FileSystemApprovalPath), nameof(ValidatePattern))]
    public string Pattern
    {
        get;
        set
        {
            value = Normalize(value);
            if (!SetProperty(ref field, value)) return;
            ValidateProperty(value);
        }
    }

    public FileSystemApprovalPath() : this(string.Empty) { }

    public FileSystemApprovalPath(string pattern) => Pattern = pattern;

    /// <summary>
    /// Normalizes a persisted glob without resolving wildcard segments against the file system.
    /// </summary>
    public static string Normalize(string? pattern)
    {
        // TODO: Approval patterns use '/' as their portable separator and trim surrounding whitespace,
        // so literal Unix names containing backslashes or leading/trailing spaces cannot be represented
        // losslessly. Keep this compatibility behavior until literal paths and globs have distinct models.
        var normalized = pattern?.Trim().Replace('\\', '/') ?? string.Empty;
        if (normalized.Length == 0) return normalized;

        var root = Path.GetPathRoot(normalized);
        if (root is null || normalized.Length > root.Length)
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    public static ValidationResult? ValidatePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);
        }

        return FileSystemApprovalGlob.TryCreate(pattern, out _) ?
            ValidationResult.Success :
            new ValidationResult(LocaleResolver.FileSystemSettings_InvalidApprovalPath_ErrorMessage);
    }
}

/// <summary>
/// Observable collection that normalizes and de-duplicates file-system approval paths.
/// </summary>
[JsonConverter(typeof(FileSystemApprovalPathCollectionJsonConverter))]
public sealed class FileSystemApprovalPathCollection : ObservableCollection<FileSystemApprovalPath>
{
    public event EventHandler? ContentsChanged;

    public FileSystemApprovalPathCollection() { }

    public FileSystemApprovalPathCollection(IEnumerable<FileSystemApprovalPath> items)
    {
        foreach (var item in items) Add(item);
    }

    public bool AddPattern(string pattern)
    {
        var item = new FileSystemApprovalPath(pattern);
        if (!string.IsNullOrWhiteSpace(item.Pattern) && ContainsPattern(item.Pattern)) return false;

        Add(item);
        return true;
    }

    public void ReplaceWith(IEnumerable<FileSystemApprovalPath> items)
    {
        var patterns = items.Select(static item => item.Pattern).ToArray();
        Clear();
        foreach (var pattern in patterns) Add(new FileSystemApprovalPath(pattern));
    }

    protected override void InsertItem(int index, FileSystemApprovalPath item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!string.IsNullOrWhiteSpace(item.Pattern) && ContainsPattern(item.Pattern)) return;

        item.PropertyChanged += HandleItemPropertyChanged;
        base.InsertItem(index, item);
        ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void RemoveItem(int index)
    {
        var item = this[index];
        item.PropertyChanged -= HandleItemPropertyChanged;
        base.RemoveItem(index);
        ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void ClearItems()
    {
        foreach (var item in this) item.PropertyChanged -= HandleItemPropertyChanged;
        base.ClearItems();
        ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void SetItem(int index, FileSystemApprovalPath item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var oldItem = this[index];
        if (!string.IsNullOrWhiteSpace(item.Pattern) && this.Any(other => !ReferenceEquals(other, oldItem) &&
                string.Equals(other.Pattern, item.Pattern, PathUtilities.SystemPathComparison)))
        {
            return;
        }

        oldItem.PropertyChanged -= HandleItemPropertyChanged;
        item.PropertyChanged += HandleItemPropertyChanged;
        base.SetItem(index, item);
        ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool ContainsPattern(string pattern) =>
        this.Any(item => string.Equals(item.Pattern, pattern, PathUtilities.SystemPathComparison));

    private void HandleItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileSystemApprovalPath.Pattern) || sender is not FileSystemApprovalPath item) return;

        if (!string.IsNullOrWhiteSpace(item.Pattern) && this.Any(other => !ReferenceEquals(other, item) &&
                string.Equals(other.Pattern, item.Pattern, PathUtilities.SystemPathComparison)))
        {
            Remove(item);
            return;
        }

        ContentsChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Serializes the approval collection as a simple JSON array of glob strings.
/// </summary>
public sealed class FileSystemApprovalPathCollectionJsonConverter : JsonConverter<FileSystemApprovalPathCollection>
{
    public override FileSystemApprovalPathCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var patterns = JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [];
        return [.. patterns.Select(static pattern => new FileSystemApprovalPath(pattern))];
    }

    public override void Write(
        Utf8JsonWriter writer,
        FileSystemApprovalPathCollection value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value) writer.WriteStringValue(item.Pattern);
        writer.WriteEndArray();
    }
}

/// <summary>
/// A compiled glob matcher rooted at the static portion of one approval path.
/// </summary>
public sealed class FileSystemApprovalGlob
{
    private readonly Matcher _matcher;

    private FileSystemApprovalGlob(string pattern, string root, Matcher matcher)
    {
        Pattern = pattern;
        Root = root;
        _matcher = matcher;
    }

    public string Pattern { get; }

    private string Root { get; }

    public bool IsMatch(string path)
    {
        return TryNormalizeCandidatePath(path, out var normalizedPath) && IsNormalizedPathMatch(normalizedPath);
    }

    public static bool TryCreate(string pattern, out FileSystemApprovalGlob? glob)
    {
        glob = null;
        try
        {
            var normalizedPattern = FileSystemApprovalPath.Normalize(pattern);
            if (!Path.IsPathFullyQualified(normalizedPattern)) return false;

            var root = Path.GetPathRoot(normalizedPattern);
            if (root.IsNullOrEmpty()) return false;
            root = FileSystemApprovalPath.Normalize(root);

            var relativePattern = normalizedPattern[root.Length..].TrimStart('/');
            if (relativePattern.IsNullOrEmpty()) relativePattern = "**";

            var matcher = new Matcher(PathUtilities.SystemPathComparison);
            matcher.AddInclude(relativePattern);
            glob = new FileSystemApprovalGlob(normalizedPattern, root, matcher);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal bool IsNormalizedPathMatch(string normalizedPath) => _matcher.Match(Root, normalizedPath).HasMatches;

    internal static bool TryNormalizeCandidatePath(string path, out string normalizedPath)
    {
        try
        {
            normalizedPath = Path.GetFullPath(path);
            if (Path.DirectorySeparatorChar != '/') normalizedPath = normalizedPath.Replace(Path.DirectorySeparatorChar, '/');
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }
}
