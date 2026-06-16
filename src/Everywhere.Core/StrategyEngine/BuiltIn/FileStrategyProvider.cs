using Everywhere.StrategyEngine.Conditions;
using Lucide.Avalonia;

namespace Everywhere.StrategyEngine.BuiltIn;

/// <summary>
/// Strategy for file attachment contexts.
/// Provides commands based on file types.
/// </summary>
public sealed class FileStrategyProvider : BuiltInStrategyProvider
{
    private static readonly string[] DocumentExtensions =
    [
        ".pdf", ".doc", ".docx", ".rtf",
        ".txt", ".md", ".ppt", ".pptx",
    ];

    private static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp",
        ".webp", ".svg", ".ico", ".tiff", ".tif"
    ];

    private static readonly string[] DataExtensions =
    [
        ".csv", ".xlsx", ".xls", ".json", ".xml",
    ];

    private static readonly string[] CodeExtensions =
    [
        ".cs", ".py", ".js", ".ts", ".jsx",
        ".tsx", ".java", ".kt", ".go", ".rs",
        ".cpp", ".c", ".h", ".hpp", ".cxx",
        ".rb", ".php", ".swift", ".scala"
    ];

    public override IEnumerable<Strategy> GetStrategies()
    {
        // Universal: Summarize file(s)
        yield return new Strategy
        {
            Id = "file-summarize",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_SummarizeCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_SummarizeCommand_Description),
            Icon = LucideIconKind.FileText,
            Priority = 100,
            Condition = new FileCondition { MinCount = 1 },
            Body =
                """
                You are an expert at analyzing and summarizing files.
                Summarize the provided file(s), highlighting:
                - Main content and purpose
                - Key information or findings
                - Notable sections or structure
                Adjust your summary based on the file type.
                """
        };

        // Document-specific commands
        yield return new Strategy
        {
            Id = "file-extract-key-points",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_ExtractKeyPointsCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_ExtractKeyPointsCommand_Description),
            Icon = LucideIconKind.ListChecks,
            Priority = 90,
            Condition = new FileCondition { Extensions = DocumentExtensions, MinCount = 1 },
            Body =
                """
                You are a document analysis expert.
                Extract the key points from the document(s):
                - Main arguments or findings
                - Important facts and figures
                - Conclusions or recommendations
                Present them as a clear, organized list.
                """
        };

        yield return new Strategy
        {
            Id = "file-translate-document",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_TranslateDocumentCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_TranslateDocumentCommand_Description),
            Icon = LucideIconKind.Languages,
            Priority = 85,
            Condition = new FileCondition { Extensions = DocumentExtensions, MinCount = 1 },
            Body =
                """
                You are a professional document translator.
                Translate the document content while:
                - Preserving formatting and structure
                - Maintaining technical accuracy
                - Keeping the original tone
                """
        };

        // Image-specific commands
        yield return new Strategy
        {
            Id = "file-describe-image",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_DescribeImageCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_DescribeImageCommand_Description),
            Icon = LucideIconKind.Image,
            Priority = 95,
            Condition = new FileCondition { Extensions = ImageExtensions, MinCount = 1 },
            Body =
                """
                You are an expert at image analysis and description.
                Describe the image in detail:
                - Main subjects and objects
                - Colors, composition, and style
                - Any text visible in the image
                - Context or setting
                """
        };

        yield return new Strategy
        {
            Id = "file-extract-text-ocr",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_ExtractTextCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_ExtractTextCommand_Description),
            Icon = LucideIconKind.ScanText,
            Priority = 90,
            Condition = new FileCondition { Extensions = ImageExtensions, MinCount = 1 },
            Body =
                """
                You are an OCR specialist.
                Extract all visible text from the image.
                Preserve the layout and structure as much as possible.
                If text is unclear, indicate uncertainty.
                """
        };

        // Data file commands
        yield return new Strategy
        {
            Id = "file-analyze-data",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_AnalyzeDataCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_AnalyzeDataCommand_Description),
            Icon = LucideIconKind.ChartBar,
            Priority = 95,
            Condition = new FileCondition { Extensions = DataExtensions, MinCount = 1 },
            Body =
                """
                You are a data analysis expert.
                Analyze the provided data and provide:
                - Overview of the data structure
                - Key statistics and patterns
                - Notable trends or anomalies
                - Actionable insights
                """
        };

        yield return new Strategy
        {
            Id = "file-visualize-data",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_VisualizeDataCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_VisualizeDataCommand_Description),
            Icon = LucideIconKind.ChartLine,
            Priority = 85,
            Condition = new FileCondition { Extensions = DataExtensions, MinCount = 1 },
            Body =
                """
                You are a data visualization expert.
                Based on the data provided:
                - Suggest appropriate chart types
                - Explain what each visualization would show
                - Provide code snippets for creating them (Python/JavaScript)
                """
        };

        // Multiple files: Compare
        yield return new Strategy
        {
            Id = "file-compare",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_CompareCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_CompareCommand_Description),
            Icon = LucideIconKind.GitCompare,
            Priority = 95,
            Condition = new FileCondition { MinCount = 2 },
            Body =
                """
                You are a file comparison expert.
                Compare the provided files and highlight:
                - Similarities and differences
                - Structural changes
                - Content additions or removals
                Present the comparison in a clear, organized format.
                """
        };

        // Code file: Analyze and explain code
        yield return new Strategy
        {
            Id = "file-analyze-code",
            NameKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_AnalyzeCodeCommand_Name),
            DescriptionKey = new DynamicLocaleKey(LocaleKey.Strategy_BuiltIn_File_AnalyzeCodeCommand_Description),
            Icon = LucideIconKind.Code,
            Priority = 90,
            Condition = new FileCondition { Extensions = CodeExtensions, MinCount = 1 },
            Body =
                """
                You are a code analysis expert.
                Analyze the provided code file(s) and explain:
                - Overall purpose and functionality
                - Key components and their roles
                - Any complex or noteworthy sections
                - Potential issues or improvements
                Provide your explanation in a clear, organized manner.
                """
        };
    }
}
