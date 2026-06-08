using Avalonia;

namespace Everywhere.Media.ImageRecognition;

public readonly record struct ImageTextRecognitionLine(PixelRect BoundingRect, string Text);