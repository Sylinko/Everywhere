namespace Everywhere.Utilities;

public enum FileTypeCategory
{
    Binary,
    Image,
    Audio,
    Video,
    Document,
    Archive,
    Script,
}

/// <summary>
/// Utility class for file-related operations, including MIME type detection and categorization.
/// </summary>
public static class FileUtilities
{
    public static IReadOnlyDictionary<string, string> KnownMimeTypes { get; } = new Dictionary<string, string>(
        141,
        StringComparer.OrdinalIgnoreCase)
    {
        // --- Images ---
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".jpe", "image/jpeg" },
        { ".jfif", "image/jpeg" },

        { ".png", "image/png" },
        { ".gif", "image/gif" },
        { ".bmp", "image/bmp" },
        { ".webp", "image/webp" },
        { ".tif", "image/tiff" },
        { ".tiff", "image/tiff" },
        { ".ico", "image/x-icon" },

        // --- Audio ---
        { ".mp3", "audio/mpeg" },
        { ".m4a", "audio/mp4" },
        { ".aac", "audio/aac" },
        { ".wav", "audio/wav" },
        { ".wave", "audio/wav" },
        { ".flac", "audio/flac" },
        { ".ogg", "audio/ogg" },
        { ".oga", "audio/ogg" },

        // --- Video ---
        { ".mp4", "video/mp4" },
        { ".m4v", "video/mp4" },
        { ".mpg", "video/mpeg" },
        { ".mpeg", "video/mpeg" },
        { ".mov", "video/quicktime" },
        { ".avi", "video/x-msvideo" },
        { ".wmv", "video/x-ms-wmv" },
        { ".webm", "video/webm" },
        { ".mkv", "video/x-matroska" },

        // --- Documents / Office ---
        { ".pdf", "application/pdf" },
        { ".doc", "application/msword" },
        { ".dot", "application/msword" },
        { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { ".dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.template" },
        { ".docm", "application/vnd.ms-word.document.macroenabled.12" },
        { ".dotm", "application/vnd.ms-word.template.macroenabled.12" },

        { ".xls", "application/vnd.ms-excel" },
        { ".xlt", "application/vnd.ms-excel" },
        { ".xla", "application/vnd.ms-excel" },
        { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { ".xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template" },
        { ".xlsm", "application/vnd.ms-excel.sheet.macroenabled.12" },
        { ".xltm", "application/vnd.ms-excel.template.macroenabled.12" },
        { ".xlam", "application/vnd.ms-excel.addin.macroenabled.12" },
        { ".xlsb", "application/vnd.ms-excel.sheet.binary.macroenabled.12" },

        { ".ppt", "application/vnd.ms-powerpoint" },
        { ".pot", "application/vnd.ms-powerpoint" },
        { ".pps", "application/vnd.ms-powerpoint" },
        { ".ppa", "application/vnd.ms-powerpoint" },
        { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { ".potx", "application/vnd.openxmlformats-officedocument.presentationml.template" },
        { ".ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow" },
        { ".pptm", "application/vnd.ms-powerpoint.presentation.macroenabled.12" },
        { ".potm", "application/vnd.ms-powerpoint.template.macroenabled.12" },
        { ".ppsm", "application/vnd.ms-powerpoint.slideshow.macroenabled.12" },

        { ".odt", "application/vnd.oasis.opendocument.text" },
        { ".ods", "application/vnd.oasis.opendocument.spreadsheet" },
        { ".odp", "application/vnd.oasis.opendocument.presentation" },

        { ".rtf", "application/rtf" },
        { ".txt", "text/plain" },
        { ".log", "text/plain" },
        { ".md", "text/markdown" },
        { ".csv", "text/csv" },
        { ".tsv", "text/tab-separated-values" },

        { ".json", "application/json" },
        { ".jsonl", "application/json" },
        { ".yaml", "application/x-yaml" },
        { ".yml", "application/x-yaml" },
        { ".toml", "application/toml" },
        { ".ini", "text/plain" },
        { ".cfg", "text/plain" },
        { ".conf", "text/plain" },
        { ".xml", "application/xml" },
        { ".xsl", "application/xml" },
        { ".xsd", "application/xml" },

        { ".html", "text/html" },
        { ".htm", "text/html" },
        { ".xhtml", "application/xhtml+xml" },

        // --- Archives / Compressed ---
        { ".zip", "application/zip" },
        { ".7z", "application/x-7z-compressed" },
        { ".rar", "application/vnd.rar" },
        { ".tar", "application/x-tar" },
        { ".gz", "application/gzip" },
        { ".tgz", "application/gzip" },
        { ".bz2", "application/x-bzip2" },
        { ".xz", "application/x-xz" },
        { ".lz", "application/x-lzip" },
        { ".lzma", "application/x-lzma" },
        { ".iso", "application/x-iso9660-image" },

        // --- Scripts / Code ---
        { ".c", "text/x-c" },
        { ".h", "text/x-c" },
        { ".cpp", "text/x-c++src" },
        { ".hpp", "text/x-c++src" },
        { ".cc", "text/x-c++src" },
        { ".cs", "text/x-csharp" },

        { ".java", "text/x-java-source" },
        { ".kt", "text/x-kotlin" },
        { ".kts", "text/x-kotlin" },

        { ".js", "text/javascript" },
        { ".mjs", "text/javascript" },
        { ".cjs", "text/javascript" },
        { ".ts", "text/typescript" },
        { ".tsx", "text/tsx" },
        { ".jsx", "text/jsx" },

        { ".py", "text/x-python" },
        { ".pyw", "text/x-python" },
        { ".rb", "text/x-ruby" },
        { ".php", "application/x-php" },
        { ".go", "text/x-go" },
        { ".rs", "text/x-rustsrc" },
        { ".swift", "text/x-swift" },

        { ".sh", "application/x-sh" },
        { ".bash", "application/x-sh" },
        { ".zsh", "application/x-sh" },
        { ".ps1", "text/x-powershell" },
        { ".psm1", "text/x-powershell" },
        { ".cmd", "text/plain" },
        { ".bat", "text/plain" },

        { ".css", "text/css" },
        { ".scss", "text/x-scss" },
        { ".sass", "text/x-sass" },
        { ".less", "text/x-less" },

        { ".sql", "application/sql" },
        { ".graphql", "application/graphql" },
        { ".yacc", "text/plain" },

        // --- Fonts ---
        { ".ttf", "font/ttf" },
        { ".otf", "font/otf" },
        { ".woff", "font/woff" },
        { ".woff2", "font/woff2" },
        { ".eot", "application/vnd.ms-fontobject" },

        // --- Binary / Executables / Libraries ---
        { ".exe", "application/vnd.microsoft.portable-executable" },
        { ".dll", "application/vnd.microsoft.portable-executable" },
        { ".sys", "application/octet-stream" },
        { ".bin", "application/octet-stream" },
        { ".dat", "application/octet-stream" },
        { ".class", "application/java-vm" },
        { ".jar", "application/java-archive" },
        { ".war", "application/java-archive" },
        { ".ear", "application/java-archive" },

        // --- Web / Misc ---
        { ".wasm", "application/wasm" },
        { ".manifest", "text/cache-manifest" },
        { ".map", "application/json" },
    };

    public static IReadOnlyDictionary<string, FileTypeCategory> KnownFileTypes { get; } = new Dictionary<string, FileTypeCategory>(
        95,
        StringComparer.OrdinalIgnoreCase)
    {
        { "image/jpeg", FileTypeCategory.Image },
        { "image/png", FileTypeCategory.Image },
        { "image/gif", FileTypeCategory.Image },
        { "image/bmp", FileTypeCategory.Image },
        { "image/webp", FileTypeCategory.Image },
        { "image/tiff", FileTypeCategory.Image },
        { "image/x-icon", FileTypeCategory.Image },

        // Audio
        { "audio/mpeg", FileTypeCategory.Audio },
        { "audio/mp4", FileTypeCategory.Audio },
        { "audio/aac", FileTypeCategory.Audio },
        { "audio/wav", FileTypeCategory.Audio },
        { "audio/flac", FileTypeCategory.Audio },
        { "audio/ogg", FileTypeCategory.Audio },

        // Video
        { "video/mp4", FileTypeCategory.Video },
        { "video/mpeg", FileTypeCategory.Video },
        { "video/quicktime", FileTypeCategory.Video },
        { "video/x-msvideo", FileTypeCategory.Video },
        { "video/x-ms-wmv", FileTypeCategory.Video },
        { "video/webm", FileTypeCategory.Video },
        { "video/x-matroska", FileTypeCategory.Video },

        // Documents
        { "application/pdf", FileTypeCategory.Document },
        { "application/msword", FileTypeCategory.Document },
        { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileTypeCategory.Document },
        { "application/vnd.openxmlformats-officedocument.wordprocessingml.template", FileTypeCategory.Document },
        { "application/vnd.ms-word.document.macroenabled.12", FileTypeCategory.Document },
        { "application/vnd.ms-word.template.macroenabled.12", FileTypeCategory.Document },

        { "application/vnd.ms-excel", FileTypeCategory.Document },
        { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileTypeCategory.Document },
        { "application/vnd.openxmlformats-officedocument.spreadsheetml.template", FileTypeCategory.Document },
        { "application/vnd.ms-excel.sheet.macroenabled.12", FileTypeCategory.Document },
        { "application/vnd.ms-excel.template.macroenabled.12", FileTypeCategory.Document },
        { "application/vnd.ms-excel.addin.macroenabled.12", FileTypeCategory.Document },
        { "application/vnd.ms-excel.sheet.binary.macroenabled.12", FileTypeCategory.Document },

        { "application/vnd.ms-powerpoint", FileTypeCategory.Document },
        { "application/vnd.openxmlformats-officedocument.presentationml.presentation", FileTypeCategory.Document },
        { "application/vnd.openxmlformats-officedocument.presentationml.template", FileTypeCategory.Document },
        { "application/vnd.openxmlformats-officedocument.presentationml.slideshow", FileTypeCategory.Document },
        { "application/vnd.ms-powerpoint.presentation.macroenabled.12", FileTypeCategory.Document },
        { "application/vnd.ms-powerpoint.template.macroenabled.12", FileTypeCategory.Document },
        { "application/vnd.ms-powerpoint.slideshow.macroenabled.12", FileTypeCategory.Document },

        { "application/vnd.oasis.opendocument.text", FileTypeCategory.Document },
        { "application/vnd.oasis.opendocument.spreadsheet", FileTypeCategory.Document },
        { "application/vnd.oasis.opendocument.presentation", FileTypeCategory.Document },

        { "text/plain", FileTypeCategory.Document },
        { "text/markdown", FileTypeCategory.Document },
        { "text/csv", FileTypeCategory.Document },
        { "text/tab-separated-values", FileTypeCategory.Document },
        { "application/rtf", FileTypeCategory.Document },
        { "application/xml", FileTypeCategory.Document },
        { "application/xhtml+xml", FileTypeCategory.Document },
        { "application/json", FileTypeCategory.Document },
        { "application/toml", FileTypeCategory.Document },
        { "application/x-yaml", FileTypeCategory.Document },
        { "application/sql", FileTypeCategory.Document },

        // Archives
        { "application/zip", FileTypeCategory.Archive },
        { "application/x-7z-compressed", FileTypeCategory.Archive },
        { "application/vnd.rar", FileTypeCategory.Archive },
        { "application/x-tar", FileTypeCategory.Archive },
        { "application/gzip", FileTypeCategory.Archive },
        { "application/x-bzip2", FileTypeCategory.Archive },
        { "application/x-xz", FileTypeCategory.Archive },
        { "application/x-lzip", FileTypeCategory.Archive },
        { "application/x-lzma", FileTypeCategory.Archive },
        { "application/x-iso9660-image", FileTypeCategory.Archive },

        // Scripts
        { "application/x-sh", FileTypeCategory.Script },
        { "text/x-powershell", FileTypeCategory.Script },
        { "application/x-php", FileTypeCategory.Script },
        { "text/javascript", FileTypeCategory.Script },
        { "text/typescript", FileTypeCategory.Script },

        { "text/x-c", FileTypeCategory.Script },
        { "text/x-c++src", FileTypeCategory.Script },
        { "text/x-csharp", FileTypeCategory.Script },
        { "text/x-java-source", FileTypeCategory.Script },
        { "text/x-kotlin", FileTypeCategory.Script },
        { "text/x-python", FileTypeCategory.Script },
        { "text/x-ruby", FileTypeCategory.Script },
        { "text/x-go", FileTypeCategory.Script },
        { "text/x-rustsrc", FileTypeCategory.Script },
        { "text/x-swift", FileTypeCategory.Script },
        { "text/x-scss", FileTypeCategory.Script },
        { "text/x-sass", FileTypeCategory.Script },
        { "text/x-less", FileTypeCategory.Script },
        { "text/css", FileTypeCategory.Script },
        { "text/html", FileTypeCategory.Script },
        { "application/graphql", FileTypeCategory.Script },

        // Fonts
        { "font/ttf", FileTypeCategory.Binary },
        { "font/otf", FileTypeCategory.Binary },
        { "font/woff", FileTypeCategory.Binary },
        { "font/woff2", FileTypeCategory.Binary },
        { "application/vnd.ms-fontobject", FileTypeCategory.Binary },

        // Binary / Executables
        { "application/vnd.microsoft.portable-executable", FileTypeCategory.Binary },
        { "application/octet-stream", FileTypeCategory.Binary },
        { "application/java-vm", FileTypeCategory.Binary },
        { "application/java-archive", FileTypeCategory.Archive },
        { "application/wasm", FileTypeCategory.Binary }
    };

    /// <summary>
    /// Detects the MIME type of a file based on its extension or content.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<string> DetectMimeTypeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // 1. detect mime type by file extension
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (KnownMimeTypes.TryGetValue(extension, out var mimeType))
        {
            return mimeType;
        }

        // 2. detect mime type by reading file header if it's not a known extension
        await using var stream = File.OpenRead(filePath);
        return await DetectMimeTypeAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Detects the MIME type of a stream by analyzing its content.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<string> DetectMimeTypeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        return await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) is null ?
            "application/octet-stream" :
            "text/plain";
    }

    /// <summary>
    /// Verifies that the given MIME type is supported. If not, throws NotSupportedException. Original MIME type is returned if supported.
    /// </summary>
    /// <param name="mimeType"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public static string VerifyMimeType(string mimeType)
    {
        return !KnownMimeTypes.Values.Contains(mimeType) ? throw new NotSupportedException($"Unsupported MIME type: {mimeType}") : mimeType;
    }

    /// <summary>
    /// Ensures that a valid MIME type is provided. If the input MIME type is null, it attempts to detect it from the file path.
    /// If detection fails, it throws NotSupportedException.
    /// </summary>
    /// <param name="mimeType"></param>
    /// <param name="filePath"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public static async Task<string> EnsureMimeTypeAsync(string? mimeType, string filePath, CancellationToken cancellationToken = default)
    {
        if (mimeType is not null) return VerifyMimeType(mimeType);

        mimeType = await DetectMimeTypeAsync(filePath, cancellationToken);
        return mimeType ?? throw new NotSupportedException($"Could not detect MIME type for file: {filePath}");
    }

    /// <summary>
    /// Checks if the given MIME type belongs to the specified file type category.
    /// </summary>
    /// <param name="mimeType"></param>
    /// <param name="category"></param>
    /// <returns></returns>
    public static bool IsOfCategory(string mimeType, FileTypeCategory category)
    {
        return KnownFileTypes.TryGetValue(mimeType, out var cat) && cat == category;
    }

    public static FileTypeCategory GetCategory(string mimeType)
    {
        return KnownFileTypes.GetValueOrDefault(mimeType, FileTypeCategory.Binary);
    }

    public static IEnumerable<string> GetMimeTypesByCategory(FileTypeCategory category)
    {
        return KnownMimeTypes
            .Where(kv => KnownFileTypes.TryGetValue(kv.Value, out var cat) && cat == category)
            .Select(kv => kv.Value);
    }
    
    public static IEnumerable<string> GetFileExtensionsByCategory(FileTypeCategory category)
    {
        return KnownMimeTypes
            .Where(kv => KnownFileTypes.TryGetValue(kv.Value, out var cat) && cat == category)
            .Select(kv => kv.Key);
    }

    public static string? GetExtensionByMimeType(string mimeType)
    {
        return KnownMimeTypes.FirstOrDefault(kv => string.Equals(kv.Value, mimeType, StringComparison.OrdinalIgnoreCase)).Key;
    }
}