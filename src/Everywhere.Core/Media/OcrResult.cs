namespace Everywhere.Media;

public readonly record struct OcrResult(IReadOnlyList<OcrLine> Lines, string Text);