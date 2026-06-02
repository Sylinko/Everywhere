namespace Everywhere.Media.Ocr;

public readonly record struct OcrResult(IReadOnlyList<OcrLine>? Lines, string? Text)
{
    public static OcrResult Empty => default;

    public bool IsEmpty => Lines is null && Text is null;
}