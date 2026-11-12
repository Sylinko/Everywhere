using ReverseMarkdown;
using System.Text;
using ZLinq;

namespace Everywhere.Web;

public enum WebExtractionSource
{
    MarkdownResponse,
    PlainTextResponse,
    AccessibilityTree,
    Readability,
    DomMainElement,
    CleanedBody
}

public readonly record struct WebPageExtractionResult(
    string Markdown,
    string? Title,
    WebExtractionSource Source,
    int ContentLength,
    WebExtractionSelection? Selection = null
)
{
    public static WebPageExtractionResult Create(string markdown, string? title, WebExtractionSource source) =>
        new(markdown, title, source, WebExtractionUtilities.CountNonWhiteSpaceCharacters(markdown));
}

public readonly record struct WebExtractionSelection(
    WebExtractionSource SelectedSource,
    IReadOnlyList<WebExtractionCandidateScore> Scores,
    double Confidence
)
{
    public static WebExtractionSelection Single(WebPageExtractionResult result) =>
        new(
            result.Source,
            [
                new WebExtractionCandidateScore(
                    result.Source,
                    result.ContentLength,
                    Score: result.ContentLength > 0 ? 1 : 0,
                    Confidence: result.ContentLength > 0 ? 1 : 0,
                    WebExtractionCandidateDiagnostics.Empty)
            ],
            result.ContentLength > 0 ? 1 : 0);
}

public readonly record struct WebExtractionCandidateScore(
    WebExtractionSource Source,
    int ContentLength,
    double Score,
    double Confidence,
    WebExtractionCandidateDiagnostics Diagnostics
);

public readonly record struct WebExtractionCandidateDiagnostics(
    int SampleLength,
    int LineCount,
    double AverageLineLength,
    double BoundedLengthScore,
    double StructureScore,
    double TextShapeScore,
    double ConsensusScore,
    double DuplicateLineRatio,
    double LinkNoiseRatio,
    double ShortLineRatio,
    double OverlongBodyPenalty
)
{
    public static WebExtractionCandidateDiagnostics Empty { get; } = new();
}

internal static class WebExtractionUtilities
{
    public const int MinimumContentLength = 100;
    public const string PreferredDocumentAcceptHeader =
        "text/markdown, text/html;q=0.9, application/xhtml+xml;q=0.9, application/xml;q=0.8, */*;q=0.7";
    private const int CandidateScoringSampleLimit = 256 * 1024;
    private const int MaxShingleCount = 512;
    private const int ShingleSize = 5;
    private const double ScoreTieThreshold = 0.03;

    public static bool HasEnoughContent(string? content) =>
        CountNonWhiteSpaceCharacters(content) >= MinimumContentLength;

    public static int CountNonWhiteSpaceCharacters(string? content)
    {
        return content?.AsValueEnumerable().Count(ch => !char.IsWhiteSpace(ch)) ?? 0;
    }

    public static Dictionary<string, string> BuildDocumentRequestHeaders(IReadOnlyDictionary<string, string> originalHeaders)
    {
        var headers = new Dictionary<string, string>(originalHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = PreferredDocumentAcceptHeader,
            ["DNT"] = "1",
            ["Sec-GPC"] = "1"
        };

        return headers;
    }

    public static WebPageExtractionResult? SelectBestFallback(IReadOnlyList<WebPageExtractionResult> candidates)
    {
        WebPageExtractionResult? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.ContentLength == 0) continue;
            if (best is null || candidate.ContentLength > best.Value.ContentLength)
            {
                best = candidate;
            }
        }

        return best;
    }

    public static WebPageExtractionResult? SelectBestCandidate(IReadOnlyList<WebPageExtractionResult> candidates)
    {
        var selection = RankCandidates(candidates);
        if (selection is null)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Source == selection.Value.SelectedSource)
            {
                return candidate with { Selection = selection.Value };
            }
        }

        return null;
    }

    public static WebExtractionSelection? RankCandidates(IReadOnlyList<WebPageExtractionResult> candidates)
    {
        if (candidates.Count == 0) return null;

        var analyses = candidates
            .AsValueEnumerable()
            .Select(AnalyzeCandidate)
            .ToArray();

        var nonEmptyLengths = analyses
            .AsValueEnumerable()
            .Where(analysis => analysis.Candidate.ContentLength > 0)
            .Select(analysis => analysis.Candidate.ContentLength)
            .Order()
            .ToArray();

        if (nonEmptyLengths.Length == 0) return null;

        var medianLength = nonEmptyLengths[nonEmptyLengths.Length / 2];
        var scored = new List<WebExtractionCandidateScore>(analyses.Length);
        foreach (var analysis in analyses.AsValueEnumerable())
        {
            var consensusScore = CalculateConsensusScore(analysis, analyses);
            var overlongBodyPenalty = CalculateOverlongBodyPenalty(analysis.Candidate, medianLength);
            var diagnostics = analysis.Diagnostics with
            {
                ConsensusScore = consensusScore,
                OverlongBodyPenalty = overlongBodyPenalty
            };
            var score = CalculateScore(analysis.Candidate.Source, diagnostics);
            var confidence = CalculateCandidateConfidence(analysis.Candidate, score, diagnostics);

            scored.Add(
                new WebExtractionCandidateScore(
                    analysis.Candidate.Source,
                    analysis.Candidate.ContentLength,
                    score,
                    confidence,
                    diagnostics));
        }

        WebExtractionCandidateScore? selected = null;
        foreach (var score in scored.AsValueEnumerable())
        {
            if (score.ContentLength == 0) continue;

            if (selected is null ||
                score.Score > selected.Value.Score + ScoreTieThreshold ||
                Math.Abs(score.Score - selected.Value.Score) <= ScoreTieThreshold &&
                GetSourcePrior(score.Source) > GetSourcePrior(selected.Value.Source))
            {
                selected = score;
            }
        }

        if (selected is null) return null;

        var secondBest = scored
            .AsValueEnumerable()
            .Where(score => score.ContentLength > 0 && score.Source != selected.Value.Source)
            .OrderByDescending(score => score.Score)
            .FirstOrDefault();

        var selectionConfidence = CalculateSelectionConfidence(selected.Value, secondBest);
        return new WebExtractionSelection(selected.Value.Source, scored, selectionConfidence);
    }

    public static string ConvertHtmlToMarkdown(string html)
    {
        var config = new Config
        {
            GithubFlavored = true,
            Tags = { Unknown = Config.UnknownTagsOption.Drop },
            Formatting = { RemoveComments = true },
            Links = { SmartHref = true }
        };
        var converter = new Converter(config);
        return NormalizeMarkdown(converter.Convert(html));
    }

    public static string NormalizeMarkdown(string? markdown)
    {
        return string.IsNullOrWhiteSpace(markdown) ? string.Empty : markdown.ReplaceLineEndings("\n").Trim();
    }

    public static string FormatSource(WebExtractionSource source) =>
        source switch
        {
            WebExtractionSource.MarkdownResponse => "markdown_response",
            WebExtractionSource.PlainTextResponse => "plain_text_response",
            WebExtractionSource.AccessibilityTree => "accessibility_tree",
            WebExtractionSource.Readability => "readability",
            WebExtractionSource.DomMainElement => "dom_main_element",
            WebExtractionSource.CleanedBody => "cleaned_body",
            _ => source.ToString()
        };

    private static CandidateAnalysis AnalyzeCandidate(WebPageExtractionResult candidate)
    {
        var sample = candidate.Markdown.Length <= CandidateScoringSampleLimit ?
            candidate.Markdown :
            candidate.Markdown[..CandidateScoringSampleLimit];

        var lineStats = AnalyzeLines(sample);
        var boundedLengthScore = CalculateBoundedLengthScore(candidate.ContentLength);
        var structureScore = CalculateStructureScore(lineStats);
        var textShapeScore = CalculateTextShapeScore(lineStats);
        var shingles = ExtractShingles(sample);

        var diagnostics = new WebExtractionCandidateDiagnostics(
            sample.Length,
            lineStats.NonEmptyLineCount,
            lineStats.AverageLineLength,
            boundedLengthScore,
            structureScore,
            textShapeScore,
            ConsensusScore: 0,
            lineStats.DuplicateLineRatio,
            lineStats.LinkNoiseRatio,
            lineStats.ShortLineRatio,
            OverlongBodyPenalty: 0);

        return new CandidateAnalysis(candidate, diagnostics, shingles);
    }

    private static LineStats AnalyzeLines(string sample)
    {
        var nonEmptyLineCount = 0;
        var totalLineLength = 0;
        var shortLineCount = 0;
        var mediumLineCount = 0;
        var linkLineCount = 0;
        var shortLinkLineCount = 0;
        var headingCount = 0;
        var listCount = 0;
        var tableCount = 0;
        var codeFenceCount = 0;
        var blockquoteCount = 0;
        var paragraphCount = 0;
        var duplicateLineCount = 0;
        var normalizedLines = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in sample.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty) continue;

            nonEmptyLineCount++;
            totalLineLength += trimmed.Length;

            if (trimmed.Length < 30) shortLineCount++;
            if (trimmed.Length is >= 45 and <= 240) mediumLineCount++;

            var normalized = NormalizeLine(trimmed);
            if (normalized.Length >= 4 && !normalizedLines.Add(normalized))
            {
                duplicateLineCount++;
            }

            if (LooksLikeLinkLine(trimmed))
            {
                linkLineCount++;
                if (trimmed.Length < 90) shortLinkLineCount++;
            }

            if (IsHeadingLine(trimmed)) headingCount++;
            else if (IsListLine(trimmed)) listCount++;
            else if (IsTableLine(trimmed)) tableCount++;
            else if (IsCodeFenceLine(trimmed)) codeFenceCount++;
            else if (trimmed.StartsWith(">")) blockquoteCount++;
            else if (trimmed.Length >= 45) paragraphCount++;
        }

        if (nonEmptyLineCount == 0)
        {
            return LineStats.Empty;
        }

        return new LineStats(
            nonEmptyLineCount,
            AverageLineLength: (double)totalLineLength / nonEmptyLineCount,
            ShortLineRatio: (double)shortLineCount / nonEmptyLineCount,
            MediumLineRatio: (double)mediumLineCount / nonEmptyLineCount,
            DuplicateLineRatio: (double)duplicateLineCount / nonEmptyLineCount,
            LinkNoiseRatio: ((double)linkLineCount + shortLinkLineCount) / (nonEmptyLineCount * 2),
            headingCount,
            listCount,
            tableCount,
            codeFenceCount,
            blockquoteCount,
            paragraphCount);
    }

    private static double CalculateBoundedLengthScore(int contentLength)
    {
        if (contentLength <= 0) return 0;
        return Clamp01(Math.Log(contentLength + 1) / Math.Log(8000));
    }

    private static double CalculateStructureScore(LineStats stats)
    {
        if (stats.NonEmptyLineCount == 0) return 0;

        var structureUnits =
            stats.HeadingCount * 1.4 +
            stats.ListCount * 0.35 +
            stats.TableCount * 0.55 +
            stats.CodeFenceCount * 0.75 +
            stats.BlockquoteCount * 0.35 +
            stats.ParagraphCount * 0.35;

        return Clamp01(1 - Math.Exp(-structureUnits / 12));
    }

    private static double CalculateTextShapeScore(LineStats stats)
    {
        if (stats.NonEmptyLineCount == 0) return 0;

        var averageLineScore = Clamp01(stats.AverageLineLength / 120);
        return Clamp01(
            averageLineScore * 0.45 +
            stats.MediumLineRatio * 0.55 -
            stats.ShortLineRatio * 0.25);
    }

    private static double CalculateConsensusScore(CandidateAnalysis analysis, IReadOnlyList<CandidateAnalysis> analyses)
    {
        if (analysis.Shingles.Count == 0) return 0.5;

        var comparisons = 0;
        var totalOverlap = 0d;
        foreach (var other in analyses)
        {
            if (ReferenceEquals(analysis, other) || other.Shingles.Count == 0) continue;

            var intersection = 0;
            foreach (var shingle in analysis.Shingles)
            {
                if (other.Shingles.Contains(shingle))
                {
                    intersection++;
                }
            }

            totalOverlap += (double)intersection / Math.Min(analysis.Shingles.Count, other.Shingles.Count);
            comparisons++;
        }

        return comparisons == 0 ? 0.5 : Clamp01(totalOverlap / comparisons);
    }

    private static double CalculateOverlongBodyPenalty(WebPageExtractionResult candidate, int medianLength)
    {
        if (candidate.Source != WebExtractionSource.CleanedBody || medianLength <= 0) return 0;
        if (candidate.ContentLength <= medianLength * 3) return 0;

        return Clamp01((candidate.ContentLength - medianLength * 3) / (double)(medianLength * 8));
    }

    private static double CalculateScore(WebExtractionSource source, WebExtractionCandidateDiagnostics diagnostics)
    {
        if (diagnostics.SampleLength == 0) return 0;

        var noisePenalty =
            diagnostics.DuplicateLineRatio * 0.14 +
            diagnostics.LinkNoiseRatio * 0.18 +
            diagnostics.ShortLineRatio * 0.10 +
            diagnostics.OverlongBodyPenalty * 0.12;

        return Clamp01(
            GetSourcePrior(source) * 0.10 +
            diagnostics.BoundedLengthScore * 0.22 +
            diagnostics.StructureScore * 0.20 +
            diagnostics.TextShapeScore * 0.20 +
            diagnostics.ConsensusScore * 0.20 -
            noisePenalty);
    }

    private static double CalculateCandidateConfidence(
        WebPageExtractionResult candidate,
        double score,
        WebExtractionCandidateDiagnostics diagnostics)
    {
        var confidence = Clamp01(
            score * 0.60 +
            diagnostics.BoundedLengthScore * 0.25 -
            (diagnostics.DuplicateLineRatio + diagnostics.LinkNoiseRatio + diagnostics.ShortLineRatio) * 0.10);

        return candidate.ContentLength < MinimumContentLength ? Math.Min(confidence, 0.35) : confidence;
    }

    private static double CalculateSelectionConfidence(
        WebExtractionCandidateScore selected,
        WebExtractionCandidateScore secondBest)
    {
        var margin = secondBest.ContentLength == 0 ? 1 : selected.Score - secondBest.Score;
        var marginScore = Clamp01(margin / 0.18);
        var noisePenalty =
            selected.Diagnostics.DuplicateLineRatio +
            selected.Diagnostics.LinkNoiseRatio +
            selected.Diagnostics.ShortLineRatio;
        var confidence = Clamp01(selected.Confidence * 0.55 + marginScore * 0.35 - noisePenalty * 0.10);

        return selected.ContentLength < MinimumContentLength ? Math.Min(confidence, 0.35) : confidence;
    }

    private static double GetSourcePrior(WebExtractionSource source) =>
        source switch
        {
            WebExtractionSource.MarkdownResponse => 1,
            WebExtractionSource.PlainTextResponse => 0.98,
            WebExtractionSource.AccessibilityTree => 0.90,
            WebExtractionSource.Readability => 0.82,
            WebExtractionSource.DomMainElement => 0.70,
            WebExtractionSource.CleanedBody => 0.55,
            _ => 0.50
        };

    private static HashSet<ulong> ExtractShingles(string sample)
    {
        var words = new List<string>(MaxShingleCount + ShingleSize);
        var builder = new StringBuilder();

        foreach (var ch in sample)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            AddWord(words, builder);
            if (words.Count >= MaxShingleCount + ShingleSize - 1) break;
        }

        AddWord(words, builder);

        var shingles = new HashSet<ulong>();
        for (var i = 0; i <= words.Count - ShingleSize && shingles.Count < MaxShingleCount; i++)
        {
            shingles.Add(HashWords(words, i, ShingleSize));
        }

        return shingles;
    }

    private static void AddWord(List<string> words, StringBuilder builder)
    {
        if (builder.Length == 0) return;
        if (builder.Length > 1)
        {
            words.Add(builder.ToString());
        }

        builder.Clear();
    }

    private static ulong HashWords(IReadOnlyList<string> words, int start, int count)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offset;
        for (var i = start; i < start + count; i++)
        {
            foreach (var ch in words[i])
            {
                hash ^= ch;
                hash *= prime;
            }

            hash ^= 0xff;
            hash *= prime;
        }

        return hash;
    }

    private static string NormalizeLine(ReadOnlySpan<char> line)
    {
        var builder = new StringBuilder(line.Length);
        var lastWasSpace = false;
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool LooksLikeLinkLine(ReadOnlySpan<char> line) =>
        Contains(line, "](") || Contains(line, "http://") || Contains(line, "https://");

    private static bool IsHeadingLine(ReadOnlySpan<char> line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#') hashes++;
        return hashes is > 0 and <= 6 && hashes < line.Length && char.IsWhiteSpace(line[hashes]);
    }

    private static bool IsListLine(ReadOnlySpan<char> line)
    {
        if (line.Length >= 2 && (line[0] is '-' or '*' or '+') && char.IsWhiteSpace(line[1])) return true;

        var index = 0;
        while (index < line.Length && char.IsDigit(line[index])) index++;
        return index > 0 &&
            index + 1 < line.Length &&
            line[index] == '.' &&
            char.IsWhiteSpace(line[index + 1]);
    }

    private static bool IsTableLine(ReadOnlySpan<char> line) =>
        line.Contains('|');

    private static bool IsCodeFenceLine(ReadOnlySpan<char> line) =>
        line.StartsWith("```") || line.StartsWith("~~~");

    private static bool Contains(ReadOnlySpan<char> value, string text) =>
        value.IndexOf(text.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;

    private static double Clamp01(double value) =>
        value switch
        {
            < 0 => 0,
            > 1 => 1,
            _ => value
        };

    private sealed record CandidateAnalysis(
        WebPageExtractionResult Candidate,
        WebExtractionCandidateDiagnostics Diagnostics,
        HashSet<ulong> Shingles
    );

    private readonly record struct LineStats(
        int NonEmptyLineCount,
        double AverageLineLength,
        double ShortLineRatio,
        double MediumLineRatio,
        double DuplicateLineRatio,
        double LinkNoiseRatio,
        int HeadingCount,
        int ListCount,
        int TableCount,
        int CodeFenceCount,
        int BlockquoteCount,
        int ParagraphCount
    )
    {
        public static LineStats Empty { get; } = new();
    }
}