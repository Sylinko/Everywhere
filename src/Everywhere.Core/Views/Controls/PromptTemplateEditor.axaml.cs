using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Everywhere.AI.Prompts;
using ZLinq;

namespace Everywhere.Views;

[TemplatePart(TextEditorPartName, typeof(TextEditor), IsRequired = true)]
public sealed class PromptTemplateEditor : TemplatedControl
{
    private const string TextEditorPartName = "PART_TextEditor";

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<PromptTemplateEditor, string?>(
            nameof(Text),
            defaultBindingMode: BindingMode.TwoWay);

    private readonly PromptPlaceholderColorizingTransformer _colorizer = new();
    private TextEditor? _textEditor;
    private bool _isUpdatingText;

    /// <summary>
    /// Template text edited by the underlying AvaloniaEdit document.
    /// </summary>
    /// <remarks>
    /// The property is owned by this control instead of binding directly to AvaloniaEdit because
    /// the editor does not expose an ordinary Avalonia two-way text property.
    /// </remarks>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_textEditor is not null)
        {
            _textEditor.TextChanged -= HandleTextEditorTextChanged;
            _textEditor.TextArea.TextView.LineTransformers.Remove(_colorizer);
        }

        _textEditor = e.NameScope.Find<TextEditor>(TextEditorPartName).NotNull();
        _textEditor.Text = Text ?? string.Empty;
        _textEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _textEditor.TextChanged += HandleTextEditorTextChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !_isUpdatingText)
        {
            ApplyTextToEditor(change.GetNewValue<string?>());
        }
    }

    private void HandleTextEditorTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextEditor textEditor)
        {
            return;
        }

        _isUpdatingText = true;
        try
        {
            SetCurrentValue(TextProperty, textEditor.Text);
        }
        finally
        {
            _isUpdatingText = false;
        }
    }

    private void ApplyTextToEditor(string? value)
    {
        if (_textEditor is not { } textEditor)
        {
            return;
        }

        var text = value ?? string.Empty;
        if (string.Equals(textEditor.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        var caretOffset = Math.Min(textEditor.CaretOffset, text.Length);
        textEditor.Text = text;
        textEditor.CaretOffset = caretOffset;
    }

    /// <summary>
    /// Colors prompt placeholders in AvaloniaEdit without enabling Markdown or TextMate highlighting.
    /// </summary>
    /// <remarks>
    /// Prompt templates are plain text. Re-parsing the current document for color slots keeps editor,
    /// read-only raw text, preview, and placeholder references on the same deterministic palette.
    /// </remarks>
    private sealed class PromptPlaceholderColorizingTransformer : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var document = CurrentContext.Document;
            var lineText = document.GetText(line.Offset, line.Length);
            var linePlaceholders = PromptTemplateParser.ParsePlaceholders(lineText);
            if (linePlaceholders.Count == 0)
            {
                return;
            }

            var colorSlots = PromptPlaceholderPalette.AssignColorSlots(
                PromptTemplateParser
                    .ParsePlaceholders(document.Text)
                    .AsValueEnumerable()
                    .Select(static placeholder => placeholder.Name)
                    .ToList());

            foreach (var placeholder in linePlaceholders)
            {
                var startOffset = line.Offset + placeholder.Span.Start;
                var endOffset = startOffset + placeholder.Span.Length;
                var colorSlot = colorSlots.TryGetValue(placeholder.Name, out var slot) ?
                    slot :
                    PromptPlaceholderPalette.UnassignedColorSlot;
                var brush = PromptPlaceholderPalette.GetBrush(placeholder.Name, colorSlot);

                ChangeLinePart(
                    startOffset,
                    endOffset,
                    element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}