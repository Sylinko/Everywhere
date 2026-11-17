using System.Text;
using Everywhere.Common;
using Everywhere.Harness;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Persists the ordered outcome of a local image view, independently of model request limits.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class ViewImageFunctionResult(IReadOnlyList<ViewImageItem> items) : ChatFunctionResult
{
    /// <summary>
    /// Gets the outcomes in the order requested by the tool call.
    /// </summary>
    [Key(0)]
    public IReadOnlyList<ViewImageItem> Items { get; } = items;

    /// <summary>
    /// Counts the successfully prepared views, including views later omitted from model input.
    /// </summary>
    [IgnoreMember]
    public int ViewedCount { get; } = items.AsValueEnumerable().Count(item => item is ViewImageItem.Success);

    /// <inheritdoc />
    public override object Value
    {
        get
        {
            var text = new StringBuilder();
            for (var index = 0; index < Items.Count; index++)
            {
                var item = Items[index];
                text.Append(index + 1).Append(". ").Append(item.SourcePath).Append(": ");
                if (item is ViewImageItem.Failure failure)
                {
                    text.Append("Could not view image: ").AppendLine(failure.Error);
                }
                else
                {
                    text.AppendLine("See the corresponding image or omission notice in the following message.");
                }
            }

            return text.ToString().TrimEnd();
        }
    }
}

/// <summary>
/// One input path and either its immutable preview or a per-file error.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
[MessagePackUnion(0, typeof(Success))]
[MessagePackUnion(1, typeof(Failure))]
public abstract partial record ViewImageItem([property: Key(0)] string SourcePath)
{
    /// <summary>
    /// Creates a successful view from a prepared image and its stored preview.
    /// </summary>
    public static Success FromPreparedImage(string sourcePath, FileAttachment preview, ChatImage image)
    {
        var isResized = image.Width != image.OriginalWidth || image.Height != image.OriginalHeight;
        return new Success(
            sourcePath,
            preview,
            image.Width,
            image.Height,
            isResized ? image.OriginalWidth : null,
            isResized ? image.OriginalHeight : null);
    }

    /// <summary>
    /// Creates a failure for a missing or blank input path.
    /// </summary>
    public static Failure EmptyPath(string? sourcePath)
    {
        return new Failure(
            sourcePath ?? string.Empty,
            "The image path is empty.",
            new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ViewImage_EmptyPath));
    }

    /// <summary>
    /// Creates an item failure with an error for the model and a localizable message for the user.
    /// </summary>
    public static Failure FromError(string sourcePath, Exception exception)
    {
        var messageKey = exception switch
        {
            ChatImage.ImageLimitException => new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ViewImage_ImageTooLarge),
            InvalidDataException => new DynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ViewImage_InvalidImage),
            _ => HandledSystemException.Handle(exception).GetFriendlyMessage()
        };
        return new Failure(sourcePath, exception.Message, messageKey);
    }

    /// <summary>
    /// A prepared image stored at its exact snapshot path. The containing tool result does
    /// not expose it as an ordinary attachment: loading a chat must not rebind it to an
    /// external file with the same hash.
    /// </summary>
    /// <param name="SourcePath">The image's original local path.</param>
    /// <param name="Preview">The stored image snapshot.</param>
    /// <param name="Width">The prepared image's width.</param>
    /// <param name="Height">The prepared image's height.</param>
    /// <param name="OriginalWidth">The width before resizing, or null if the dimensions did not change.</param>
    /// <param name="OriginalHeight">The height before resizing, or null if the dimensions did not change.</param>
    [MessagePackObject(OnlyIncludeKeyedMembers = true)]
    public sealed record Success(
        string SourcePath,
        [property: Key(1)] FileAttachment Preview,
        [property: Key(2)] int Width,
        [property: Key(3)] int Height,
        [property: Key(4)] int? OriginalWidth,
        [property: Key(5)] int? OriginalHeight
    ) : ViewImageItem(SourcePath)
    {
        [IgnoreMember]
        public string Name => Path.GetFileName(SourcePath);
    }

    /// <summary>
    /// One rejected path, with separate model-facing and localized UI messages.
    /// </summary>
    /// <param name="SourcePath">The rejected path, or an empty string if no path was supplied.</param>
    /// <param name="Error">The model-facing error message.</param>
    /// <param name="ErrorMessageKey">The localizable message shown in the UI.</param>
    [MessagePackObject(OnlyIncludeKeyedMembers = true)]
    public sealed record Failure(
        string SourcePath,
        [property: Key(1)] string Error,
        [property: Key(2)] IDynamicLocaleKey ErrorMessageKey
    ) : ViewImageItem(SourcePath);
}