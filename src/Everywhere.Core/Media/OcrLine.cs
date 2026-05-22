namespace Everywhere.Media;

public readonly record struct OcrLine(PixelRect BoundingRect, string Text);