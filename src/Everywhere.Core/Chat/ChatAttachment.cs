using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Utilities;
using Lucide.Avalonia;
using MessagePack;
using Serilog;

namespace Everywhere.Chat;

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[Union(0, typeof(VisualElementAttachment))]
[Union(1, typeof(TextSelectionAttachment))]
[Union(2, typeof(TextAttachment))]
[Union(3, typeof(FileAttachment))]
public abstract partial class ChatAttachment(IDynamicLocaleKey headerKey) : ObservableObject
{
    public abstract LucideIconKind Icon { get; }

    [Key(0)]
    public virtual IDynamicLocaleKey HeaderKey => headerKey;

    /// <summary>
    /// Indicates whether the attachment is presently focused in the UI.
    /// </summary>
    [IgnoreMember]
    public bool IsPrimary { get; set; }

    /// <summary>
    /// The opacity that bind to the view for animation.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    public partial double Opacity { get; set; } = 1d;
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class VisualElementAttachment : ChatAttachment
{
    [Key(1)]
    public override LucideIconKind Icon { get; }

    /// <summary>
    /// The text representation of the visual element.
    /// </summary>
    [Key(2)]
    public string? Content { get; set; }

    /// <summary>
    /// Ignore this property during serialization because it should already be converted into prompts and shouldn't appear in history.
    /// </summary>
    [IgnoreMember]
    public ResilientReference<IVisualElement>? Element { get; }

    /// <summary>
    /// Indicates whether the visual element is valid.
    /// </summary>
    [IgnoreMember]
    public bool IsElementValid => Element?.Target is not null;

    [SerializationConstructor]
    protected VisualElementAttachment(IDynamicLocaleKey headerKey, LucideIconKind icon) : base(headerKey)
    {
        Icon = icon;
    }

    protected VisualElementAttachment(IDynamicLocaleKey headerKey, LucideIconKind icon, IVisualElement? element) : base(headerKey)
    {
        Icon = icon;
        Element = element is null ? null : new ResilientReference<IVisualElement>(element);
    }

    public static VisualElementAttachment FromVisualElement(IVisualElement element)
    {
        DynamicLocaleKey headerKey;
        var elementTypeKey = new DynamicLocaleKey($"VisualElementType_{element.Type}");
        if (element.ProcessId > 0)
        {
            try
            {
                using var process = Process.GetProcessById(element.ProcessId);
                headerKey = new FormattedDynamicLocaleKey("{0} - {1}", new DirectLocaleKey(process.ProcessName), elementTypeKey);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                headerKey = elementTypeKey;
            }
        }
        else
        {
            headerKey = elementTypeKey;
        }

        return new VisualElementAttachment(
            headerKey,
            element.Type switch
            {
                VisualElementType.Label => LucideIconKind.Type,
                VisualElementType.TextEdit => LucideIconKind.TextInitial,
                VisualElementType.Document => LucideIconKind.FileText,
                VisualElementType.Image => LucideIconKind.Image,
                VisualElementType.CheckBox => LucideIconKind.SquareCheck,
                VisualElementType.RadioButton => LucideIconKind.CircleCheckBig,
                VisualElementType.ComboBox => LucideIconKind.ChevronDown,
                VisualElementType.ListView => LucideIconKind.List,
                VisualElementType.ListViewItem => LucideIconKind.List,
                VisualElementType.TreeView => LucideIconKind.ListTree,
                VisualElementType.TreeViewItem => LucideIconKind.ListTree,
                VisualElementType.DataGrid => LucideIconKind.Table,
                VisualElementType.DataGridItem => LucideIconKind.Table,
                VisualElementType.TabControl or VisualElementType.TabItem => LucideIconKind.LayoutPanelTop,
                VisualElementType.Table => LucideIconKind.Table,
                VisualElementType.TableRow => LucideIconKind.Table,
                VisualElementType.Menu => LucideIconKind.Menu,
                VisualElementType.MenuItem => LucideIconKind.Menu,
                VisualElementType.Slider => LucideIconKind.SlidersHorizontal,
                VisualElementType.ScrollBar => LucideIconKind.Settings2,
                VisualElementType.ProgressBar => LucideIconKind.Percent,
                VisualElementType.Panel => LucideIconKind.Group,
                VisualElementType.TopLevel => LucideIconKind.AppWindow,
                VisualElementType.Screen => LucideIconKind.Monitor,
                _ => LucideIconKind.Component
            },
            element);
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class TextSelectionAttachment : VisualElementAttachment
{
    /// <summary>
    /// Override to prevent serialization of HeaderKey.
    /// </summary>
    [IgnoreMember]
    public override IDynamicLocaleKey HeaderKey => base.HeaderKey;

    [IgnoreMember]
    public override LucideIconKind Icon => LucideIconKind.TextCursorInput;

    [Key(0)]
    public string Text { get; }

    [SerializationConstructor]
    private TextSelectionAttachment(string text) : base(CreateHeaderKey(text), LucideIconKind.TextSelect)
    {
        Text = text;
        IsPrimary = true;
    }

    public TextSelectionAttachment(string text, IVisualElement? element) : base(
        CreateHeaderKey(text),
        LucideIconKind.TextSelect,
        element)
    {
        Text = text;
        IsPrimary = true;
    }

    /// <summary>
    /// Creates a header key based on the provided text. It trims the middle part of the text to 30 characters if it's too long.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static DirectLocaleKey CreateHeaderKey(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new DirectLocaleKey(string.Empty);
        }

        const int MaxLength = 30;
        const string MiddlePart = " ";

        var resultLength = text.Length <= MaxLength ? text.Length : MaxLength;

        var result = string.Create(resultLength, text, (span, state) =>
        {
            if (state.Length <= MaxLength)
            {
                for (var i = 0; i < state.Length; i++)
                {
                    var c = state[i];
                    span[i] = c is '\r' or '\n' or '\t' ? ' ' : c;
                }
            }
            else
            {
                var prefixLength = (MaxLength - MiddlePart.Length) / 2;
                var suffixLength = MaxLength - MiddlePart.Length - prefixLength;

                for (var i = 0; i < prefixLength; i++)
                {
                    var c = state[i];
                    span[i] = c is '\r' or '\n' or '\t' ? ' ' : c;
                }

                MiddlePart.AsSpan().CopyTo(span.Slice(prefixLength, MiddlePart.Length));

                var suffixStart = state.Length - suffixLength;
                var destStart = prefixLength + MiddlePart.Length;

                for (var i = 0; i < suffixLength; i++)
                {
                    var c = state[suffixStart + i];
                    span[destStart + i] = c is '\r' or '\n' or '\t' ? ' ' : c;
                }
            }
        });

        return new DirectLocaleKey(result);
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class TextAttachment(IDynamicLocaleKey headerKey, string text) : ChatAttachment(headerKey)
{
    public override LucideIconKind Icon => LucideIconKind.TextInitial;

    [Key(1)]
    public string Text => text;
}

/// <summary>
/// Represents a file attachment in a chat message.
/// Supports image, video, audio, document, and plain file types.
/// </summary>
/// <param name="headerKey"></param>
/// <param name="filePath"></param>
/// <param name="sha256"></param>
/// <param name="mimeType"></param>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class FileAttachment(
    IDynamicLocaleKey headerKey,
    string filePath,
    string sha256,
    string mimeType,
    string? description = null
) : ChatAttachment(headerKey)
{
    public override LucideIconKind Icon => LucideIconKind.File;

    [Key(1)]
    public string FilePath
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            _isImageLoaded = false;
        }
    } = filePath;

    [Key(2)]
    public string Sha256 { get; } = sha256;

    [Key(3)]
    public string MimeType { get; } = FileUtilities.VerifyMimeType(mimeType);

    /// <summary>
    /// Extra description for the file attachment passed to the LLM.
    /// e.g. From clipboard, downloaded from URL, etc.
    /// </summary>
    [Key(4)]
    public string? Description { get; set; } = description;

    [JsonIgnore]
    [IgnoreMember]
    public bool IsImage => FileUtilities.IsOfCategory(MimeType, FileTypeCategory.Image);

    [JsonIgnore]
    [IgnoreMember]
    public Bitmap? Image
    {
        get
        {
            if (_isImageLoaded) return field;

            Task.Run(async () =>
            {
                try
                {
                    field = await LoadImageAsync();
                }
                catch (Exception ex)
                {
                    ex = HandledSystemException.Handle(ex);
                    Log.Logger.ForContext<FileAttachment>().Error(ex, "Failed to load image from file: {FilePath}", FilePath);
                    field = null;
                }
                finally
                {
                    _isImageLoaded = true;
                    OnPropertyChanged();
                }
            });

            return field;

            async ValueTask<Bitmap?> LoadImageAsync()
            {
                const int maxWidth = 512;
                const int maxHeight = 512;

                if (!IsImage) return null;
                if (!File.Exists(FilePath)) return null;

                await using var stream = File.OpenRead(FilePath);
                var bitmap = Bitmap.DecodeToWidth(stream, maxWidth);
                return await ResizeImageOnDemandAsync(bitmap, maxWidth, maxHeight).ConfigureAwait(false);
            }
        }
    }

    [JsonIgnore]
    [IgnoreMember]
    public Modalities? RequiredModalities => FileUtilities.GetCategory(MimeType) switch
    {
        FileTypeCategory.Image => Modalities.Image,
        FileTypeCategory.Audio => Modalities.Audio,
        FileTypeCategory.Video => Modalities.Video,
        FileTypeCategory.Document when MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) => Modalities.Pdf,
        _ => null
    };

    [JsonIgnore]
    [IgnoreMember]
    private bool _isImageLoaded;

    public override string ToString()
    {
        return $"{MimeType}: {Path.GetFileName(FilePath)}";
    }

    /// <summary>
    /// Creates a new FileAttachment from a file path.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="mimeType">null for auto-detection</param>
    /// <param name="description"></param>
    /// <param name="maxBytesSize"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown if the file does not exist.
    /// </exception>
    /// <exception cref="OverflowException">
    /// Thrown if the file size exceeds the maximum allowed size.
    /// </exception>
    public static Task<FileAttachment> CreateAsync(
        string filePath,
        string? mimeType = null,
        string? description = null,
        long maxBytesSize = 25L * 1024 * 1024,
        CancellationToken cancellationToken = default) => Task.Run(
        async () =>
        {
            await using var stream = File.OpenRead(filePath);
            if (stream.Length > maxBytesSize)
            {
                throw new HandledException(
                    new NotSupportedException(
                        $"The file '{filePath}' is too large to attach. Its size exceeds the maximum allowed size of {Humanizer.HumanizeBytes(maxBytesSize)}."),
                    new FormattedDynamicLocaleKey(
                        LocaleKey.FileAttachment_Create_FileTooLarge,
                        new DirectLocaleKey(Humanizer.HumanizeBytes(stream.Length)),
                        new DirectLocaleKey(Humanizer.HumanizeBytes(maxBytesSize))),
                    showDetails: false);
            }

            mimeType = await FileUtilities.EnsureMimeTypeAsync(mimeType, filePath, cancellationToken);
            var sha256 = await SHA256.HashDataAsync(stream, cancellationToken);
            var sha256String = Convert.ToHexString(sha256).ToLowerInvariant();
            return new FileAttachment(new DirectLocaleKey(Path.GetFileName(filePath)), filePath, sha256String, mimeType, description);
        },
        cancellationToken);

    private async static ValueTask<Bitmap> ResizeImageOnDemandAsync(Bitmap image, int maxWidth = 2560, int maxHeight = 2560)
    {
        if (image.PixelSize.Width <= maxWidth && image.PixelSize.Height <= maxHeight)
        {
            return image;
        }

        var scale = Math.Min(maxWidth / (double)image.PixelSize.Width, maxHeight / (double)image.PixelSize.Height);
        var newWidth = (int)(image.PixelSize.Width * scale);
        var newHeight = (int)(image.PixelSize.Height * scale);

        return await Task.Run(() => image.CreateScaledBitmap(new PixelSize(newWidth, newHeight)));
    }
}