using Avalonia;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Media.ImageRecognition;
using Vision;
using ZLinq;

namespace Everywhere.Mac.Media;

public sealed class VNImageTextRecognitionEngine : IImageTextRecognitionEngine
{
    public string Id => "apple.vision";
    public bool IsSupported { get; }

    public ImageTextRecognitionEngineDescriptor Descriptor { get; } = new(
        new DirectResourceKey("Apple Vision OCR"), // No need to i18n
        new DynamicResourceKey(""),
        true,
        true);

    public IReadOnlyBindableList<DynamicNotification> Notifications { get; set; } = new BindableList<DynamicNotification>();

    public IReadOnlyList<LocaleName> SupportedLocales { get; set; } = [];

    public VNImageTextRecognitionEngine()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(10, 15))
        {
            IsSupported = false;
            return;
        }

        using var request = new VNRecognizeTextRequest(null);
        var supportedLanguages = request.GetSupportedRecognitionLanguages(out var error);
        if (error is not null || supportedLanguages is not { Length: > 0 })
        {
            // throw new NSErrorException(error);
            IsSupported = false;
            return;
        }
    }

    public Task<ImageTextRecognitionResult> RecognizeAsync(string filePath, LocaleName locale, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Apple Vision OCR requires macOS 10.15 or later.");
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var fileStream = File.OpenRead(filePath);
                using var image = NSImage.FromStream(fileStream) ?? throw new InvalidOperationException($"Failed to load image: {filePath}");
                var cgImage = image.CGImage ?? throw new InvalidOperationException("Failed to create CGImage.");

                return Recognize(cgImage, locale, cancellationToken);
            },
            cancellationToken);
    }

    private static ImageTextRecognitionResult Recognize(CGImage cgImage, LocaleName locale, CancellationToken cancellationToken)
    {
        VNRecognizedTextObservation[]? observations = null;

        using var request = new VNRecognizeTextRequest((request, error) =>
        {
            if (error is not null)
            {
                throw new NSErrorException(error);
            }

            observations = request.GetResults<VNRecognizedTextObservation>();
        });

        cancellationToken.ThrowIfCancellationRequested();

        request.RecognitionLevel = VNRequestTextRecognitionLevel.Accurate;
        request.RecognitionLanguages = [locale.ToString(), "en"];
        request.UsesLanguageCorrection = true;

        using var handler = new VNImageRequestHandler(cgImage, new VNImageOptions());
        handler.Perform([request], out var performError);

        cancellationToken.ThrowIfCancellationRequested();

        if (performError is not null)
        {
            throw new NSErrorException(performError);
        }

        return ConvertToOcrResult(observations, (int)cgImage.Width, (int)cgImage.Height);
    }

    private static ImageTextRecognitionResult ConvertToOcrResult(VNRecognizedTextObservation[]? observations, int imagePixelWidth, int imagePixelHeight)
    {
        if (observations is not { Length: > 0 })
        {
            return ImageTextRecognitionResult.Empty;
        }

        var lines = new List<ImageTextRecognitionLine>(observations.Length);

        foreach (var observation in observations)
        {
            var candidates = observation.TopCandidates(1);
            if (candidates is not { Length: > 0 })
                continue;

            var candidate = candidates[0];
            var text = candidate.String;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var rect = ConvertVisionBoundingBoxToPixelRect(
                observation.BoundingBox,
                imagePixelWidth,
                imagePixelHeight);

            lines.Add(new ImageTextRecognitionLine(
                BoundingRect: rect,
                Text: text
            ));
        }

        if (lines.Count == 0)
            return ImageTextRecognitionResult.Empty;

        var sortedLines = SortLinesByReadingOrder(lines);

        var fullText = string.Join(
            Environment.NewLine,
            sortedLines.Select(x => x.Text));

        return new ImageTextRecognitionResult(sortedLines, fullText);
    }

    private static PixelRect ConvertVisionBoundingBoxToPixelRect(CGRect box, int imagePixelWidth, int imagePixelHeight)
    {
        var x = box.X * imagePixelWidth;
        var y = (1.0 - box.Y - box.Height) * imagePixelHeight;
        var width = box.Width * imagePixelWidth;
        var height = box.Height * imagePixelHeight;

        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var right = (int)Math.Ceiling(x + width);
        var bottom = (int)Math.Ceiling(y + height);

        left = Math.Clamp(left, 0, imagePixelWidth);
        top = Math.Clamp(top, 0, imagePixelHeight);
        right = Math.Clamp(right, 0, imagePixelWidth);
        bottom = Math.Clamp(bottom, 0, imagePixelHeight);

        return new PixelRect(
            left,
            top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top)
        );
    }

    /// <summary>
    /// Sorts OCR lines in a natural reading order (top-to-bottom, left-to-right) with tolerance for minor vertical misalignments.
    /// Lines that are close enough vertically are grouped together and sorted horizontally within the group.
    /// This helps maintain the correct reading order even when lines are not perfectly aligned.
    /// </summary>
    /// <param name="lines"></param>
    /// <returns></returns>
    private static List<ImageTextRecognitionLine> SortLinesByReadingOrder(List<ImageTextRecognitionLine> lines)
    {
        if (lines.Count <= 1)
            return lines;

        var result = new List<ImageTextRecognitionLine>(lines.Count);
        var groups = new List<List<ImageTextRecognitionLine>>();

        foreach (var line in lines.AsValueEnumerable().OrderBy(line => line.BoundingRect.Y + line.BoundingRect.Height / 2.0))
        {
            var centerY = GetCenterY(line.BoundingRect);
            List<ImageTextRecognitionLine>? targetGroup = null;

            foreach (var group in groups)
            {
                var groupCenterY = group.Average(x => GetCenterY(x.BoundingRect));
                var groupMedianHeight = Median(group.Select(x => Math.Max(1, x.BoundingRect.Height)));
                var tolerance = Math.Max(4.0, groupMedianHeight * 0.5);

                if (Math.Abs(centerY - groupCenterY) <= tolerance)
                {
                    targetGroup = group;
                    break;
                }
            }

            if (targetGroup is null)
            {
                targetGroup = [];
                groups.Add(targetGroup);
            }

            targetGroup.Add(line);
        }

        result.AddRange(groups.OrderBy(g => g.Average(x => GetCenterY(x.BoundingRect))).SelectMany(group => group.OrderBy(x => x.BoundingRect.X)));

        return result;
    }

    private static double GetCenterY(PixelRect rect)
    {
        return rect.Y + rect.Height / 2.0;
    }

    private static double Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(x => x).ToArray();

        if (sorted.Length == 0)
            return 0;

        var mid = sorted.Length / 2;

        if (sorted.Length % 2 == 1)
            return sorted[mid];

        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}