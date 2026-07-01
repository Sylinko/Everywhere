using System.Diagnostics;
using System.Security;
using System.Text;
using Everywhere.Interop;
using ZLinq;

namespace Everywhere.Chat;

partial class VisualContextBuilder
{
    private string GenerateXmlString(Dictionary<string, VisualElementNode> visualElements)
    {
        var sb = new StringBuilder();
        foreach (var rootElement in visualElements.Values.AsValueEnumerable().Where(e => e.Parent is null))
        {
            if (rootElement.Type is not VisualElementType.TopLevel and not VisualElementType.Screen)
            {
                // Append a synthetic root for non-top-level elements
                var topLevelOrScreenElement = rootElement.Element.Parent;
                while (topLevelOrScreenElement is { Type: not VisualElementType.TopLevel and not VisualElementType.Screen, Parent: { } parent })
                {
                    topLevelOrScreenElement = parent;
                }

                if (topLevelOrScreenElement is not null)
                {
                    // Create a synthetic root element and build its XML
                    var actualRootElement = new VisualElementNode(
                        topLevelOrScreenElement,
                        topLevelOrScreenElement.Type,
                        null,
                        0,
                        null,
                        ["<!-- Child elements omitted for brevity -->"],
                        8,
                        0,
                        true,
                        false)
                    {
                        Children = { rootElement }
                    };
                    BuildXml(sb, actualRootElement, 0);
                    continue;
                }
            }

            BuildXml(sb, rootElement, 0);
        }

        return sb.TrimEnd().ToString();
    }

    private void BuildXml(StringBuilder sb, VisualElementNode elementNode, int indentLevel)
    {
        var element = elementNode.Element;
        var elementType = elementNode.Type;
        var indent = new string(' ', indentLevel * 2);

        // If not active, we don't render this element's tags, but we might render its children.
        // This acts as a "passthrough" for structural containers that are not interesting enough to show.
        // For TopLevel and Screen elements, we always render them even if not visible.
        if (!elementNode.IsVisible && elementType is not VisualElementType.TopLevel and not VisualElementType.Screen)
        {
            foreach (var child in elementNode.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex))
            {
                BuildXml(sb, child, indentLevel);
            }

            return;
        }

        // Start tag
        sb.Append(indent).Append('<').Append(elementType);

        // Add ID
        var id = BuiltVisualElements.Count + startingId;
        BuiltVisualElements[id] = element;
        sb.Append(" id=\"").Append(id).Append('"');

        // Add coreElement attribute if applicable
        if (elementNode.IsImportant)
        {
            sb.Append(" important=\"true\"");
        }

        // Add bounds if needed
        if (ShouldIncludeBounds(detailLevel, elementType))
        {
            // for containers, include the element's size
            var bounds = element.BoundingRectangle;
            sb.Append(" box=\"")
                .Append(bounds.X).Append(',')
                .Append(bounds.Y).Append(',')
                .Append(bounds.Width).Append(',')
                .Append(bounds.Height).Append('"');
        }

        // For top-level elements, add pid, process name and WindowHandle attributes
        if (elementType == VisualElementType.TopLevel)
        {
            var processId = elementNode.Element.ProcessId;
            if (processId > 0)
            {
                sb.Append(" pid=\"").Append(processId).Append('"');
                try
                {
                    using var process = Process.GetProcessById(processId);
                    sb.Append(" process=\"").Append(SecurityElement.Escape(process.ProcessName)).Append('"');
                }
                catch
                {
                    // Ignore if process not found
                }
            }

            var windowHandle = elementNode.Element.NativeWindowHandle;
            if (windowHandle > 0)
            {
                sb.Append(" hwnd=\"0x").Append(windowHandle.ToString("X")).Append('"');
            }
        }

        if (elementNode.Description != null)
        {
            sb.Append(" description=\"").Append(SecurityElement.Escape(elementNode.Description)).Append('"');
        }

        // Add content attribute if there's a 1 or 2 line content
        if (elementNode.ContentLines.Count is > 0 and < 3)
        {
            sb.Append(" content=\"").Append(SecurityElement.Escape(string.Join('\n', elementNode.ContentLines))).Append('"');
        }

        if (elementNode.Children.Count == 0 && elementNode.ContentLines.Count < 3 && !elementNode.HasOmittedChildren)
        {
            // Self-closing tag if no children, no content, and nothing omitted
            sb.Append("/>").AppendLine();
            return;
        }

        sb.Append('>').AppendLine();
        var xmlLengthBeforeContent = sb.Length;

        // Add contents if there are 3 or more lines
        if (elementNode.ContentLines.Count >= 3)
        {
            foreach (var contentLine in elementNode.ContentLines.AsValueEnumerable())
            {
                if (string.IsNullOrWhiteSpace(contentLine))
                {
                    sb.AppendLine(); // don't write indentation for empty lines
                    continue;
                }

                sb.Append(indent).Append("  ").Append(SecurityElement.Escape(contentLine)).AppendLine();
            }
        }

        // Handle child elements
        foreach (var child in elementNode.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex))
        {
            BuildXml(sb, child, indentLevel + 1);
        }

        // Add omission hint for the LLM
        if (elementNode.HasOmittedChildren)
        {
            sb.Append(indent)
                .Append("  <!-- more children omitted, use get_visual_tree(elementId=")
                .Append(id)
                .Append(") to expand -->")
                .AppendLine();
        }

        if (xmlLengthBeforeContent == sb.Length)
        {
            // No content or children were added, so we can convert to self-closing tag
            sb.Length -= Environment.NewLine.Length + 1; // Remove the newline and '>'
            sb.Append("/>").AppendLine();
            return;
        }

        // End tag
        sb.Append(indent).Append("</").Append(element.Type).Append('>').AppendLine();
    }
}