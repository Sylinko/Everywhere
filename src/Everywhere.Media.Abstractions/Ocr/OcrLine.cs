using Avalonia;

namespace Everywhere.Media.Ocr;

public readonly record struct OcrLine(PixelRect BoundingRect, string Text, double Confidence = double.NaN);