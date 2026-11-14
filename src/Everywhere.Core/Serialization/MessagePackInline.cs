using Avalonia.Controls.Documents;
using Avalonia.Media;
using MessagePack;

namespace Everywhere.Serialization;

/// <summary>
/// Represents an inline element in a message, such as text or line breaks.
/// </summary>
[MessagePackObject]
[MessagePackUnion(0, typeof(MessagePackRun))]
[MessagePackUnion(1, typeof(MessagePackLineBreak))]
public abstract partial class MessagePackInline
{
    public abstract Inline ToInline();

    public static MessagePackInline FromInline(Inline inline)
    {
        return inline switch
        {
            Run run => new MessagePackRun(run),
            LineBreak => new MessagePackLineBreak(),
            _ => throw new NotSupportedException($"Unsupported inline type: {inline.GetType()}")
        };
    }
}

/// <summary>
/// Represents a text run inline element for MessagePack serialization.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
public partial class MessagePackRun : MessagePackInline
{
    /// <summary>
    /// Gets or sets the text content of the run.
    /// </summary>
    [Key(0)]
    private string? Text { get; set; }

    [Key(1)]
    private TextDecorationLocation[]? TextDecorationLocations { get; set; }

    [SerializationConstructor]
    private MessagePackRun() { }

    public MessagePackRun(Run run)
    {
        Text = run.Text;
        TextDecorationLocations = run.TextDecorations?.Select(td => td.Location).ToArray();
    }

    /// <summary>
    /// Converts this MessagePackRun to an Avalonia Run inline.
    /// </summary>
    /// <returns>A Run inline with the text content.</returns>
    public override Inline ToInline()
    {
        return new Run(Text)
        {
            TextDecorations = TextDecorationLocations switch
            {
                [TextDecorationLocation.Underline] => TextDecorations.Underline,
                [TextDecorationLocation.Strikethrough] => TextDecorations.Strikethrough,
                [TextDecorationLocation.Overline] => TextDecorations.Overline,
                [TextDecorationLocation.Baseline] => TextDecorations.Baseline,
                null => null,
                _ => new TextDecorationCollection(TextDecorationLocations.Select(location => new TextDecoration { Location = location }))
            }
        };
    }
}

/// <summary>
/// Represents a line break inline element for MessagePack serialization.
/// </summary>
[MessagePackObject]
public partial class MessagePackLineBreak : MessagePackInline
{
    /// <summary>
    /// Converts this MessagePackLineBreak to an Avalonia LineBreak inline.
    /// </summary>
    /// <returns>A LineBreak inline.</returns>
    public override Inline ToInline()
    {
        return new LineBreak();
    }
}
