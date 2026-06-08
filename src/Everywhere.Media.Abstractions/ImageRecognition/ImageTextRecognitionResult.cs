namespace Everywhere.Media.ImageRecognition;

public readonly record struct ImageTextRecognitionResult(IReadOnlyList<ImageTextRecognitionLine>? Lines, string? Text)
{
    public static ImageTextRecognitionResult Empty => default;

    public bool IsEmpty => Lines is null && Text is null;
}