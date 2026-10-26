using System.Text;

namespace Everywhere.Chat;

public static class ChatDataExporter
{
    // TODO: make this a proper export service with DI and support for multiple formats (Markdown, HTML, PDF, etc.)
    // TODO: add support for exporting attachments (images, files, etc.) and include them in the exported file or as separate files.
    // TODO: make it more beautiful and readable with proper formatting, headings, and styling.
    public static async Task ExportAsMarkdown(ChatContext context, Stream outputStream, CancellationToken cancellationToken = default)
    {
        // Export the branch chosen when the operation began, even if the view switches while
        // historical bodies are loading. Other turns and their branches remain cold.
        var chatData = context.Data;
        var path = chatData.Items;
        foreach (var turn in path.Select(node => node.Turn).OfType<ChatTurn>().Distinct())
        {
            await context.EnsureTurnLoadedAsync(turn, cancellationToken);
        }

        if (path.Any(node => node.Turn is null && !node.IsMessageLoaded))
        {
            await context.EnsureTurnLoadedAsync(null, cancellationToken);
        }

        var markdownBuilder = new StringBuilder();
        var metadata = chatData.Metadata;

        markdownBuilder
            .Append("# ")
            .AppendLine(metadata.Topic ?? LocaleKey.ChatContext_Metadata_Topic_Default.I18N())
            .AppendLine();

        markdownBuilder
            .Append("**")
            .Append(LocaleKey.ChatWindowViewModel_ExportMarkdown_DateCreated.I18N())
            .Append(":** ")
            .AppendLine(metadata.DateCreated.ToString("F"))
            .AppendLine();

        markdownBuilder
            .Append("**")
            .Append(LocaleKey.ChatWindowViewModel_ExportMarkdown_DateModified.I18N())
            .Append(":** ")
            .AppendLine(metadata.DateModified.ToString("F"))
            .AppendLine();

        markdownBuilder.AppendLine("---").AppendLine();

        foreach (var chatMessage in path.Select(node => node.Message))
        {
            switch (chatMessage)
            {
                case UserChatMessage user:
                {
                    markdownBuilder
                        .Append("## 👤 ")
                        .AppendLine(LocaleKey.ChatWindowViewModel_ExportMarkdown_UserRole.I18N())
                        .AppendLine()
                        .AppendLine(user.Content)
                        .AppendLine();

                    if (user.Attachments.Any())
                    {
                        markdownBuilder
                            .Append("**")
                            .Append(LocaleKey.ChatWindowViewModel_ExportMarkdown_UserAttachments.I18N())
                            .AppendLine(":**")
                            .AppendLine();
                        foreach (var attachment in user.Attachments)
                        {
                            markdownBuilder
                                .Append("- ")
                                .AppendLine(attachment.HeaderKey.ToString())
                                .AppendLine();
                        }
                    }

                    markdownBuilder.AppendLine();
                    break;
                }
                case AssistantChatMessage assistant:
                {
                    markdownBuilder
                        .Append("## 🤖 ")
                        .AppendLine(LocaleKey.ChatWindowViewModel_ExportMarkdown_AssistantRole.I18N())
                        .AppendLine();

                    foreach (var span in assistant.Items.AsValueEnumerable())
                    {
                        switch (span)
                        {
                            case AssistantChatMessageTextSpan { Content: { Length: > 0 } content }:
                            {
                                markdownBuilder.AppendLine(content).AppendLine();
                                break;
                            }
                            case AssistantChatMessageFunctionCallSpan { Items: { Count: > 0 } functionCalls }:
                            {
                                foreach (var functionCall in functionCalls.AsValueEnumerable())
                                {
                                    markdownBuilder
                                        .Append("🛠️ **")
                                        .Append(LocaleKey.ChatWindowViewModel_ExportMarkdown_FunctionCall.I18N())
                                        .Append(":** ")
                                        .AppendLine(functionCall.HeaderKey?.ToString())
                                        .AppendLine();

                                    foreach (var result in functionCall.Results
                                                 .AsValueEnumerable()
                                                 .Select(static result => result.Result is ChatFunctionResult structuredResult ?
                                                     structuredResult.Value?.ToString() :
                                                     result.Result?.ToString())
                                                 .Where(r => !r.IsNullOrEmpty()))
                                    {
                                        markdownBuilder
                                            .AppendLine("```")
                                            .AppendLine(result)
                                            .AppendLine("```")
                                            .AppendLine();
                                    }

                                    if (functionCall.ErrorMessageKey?.ToString() is { Length: > 0 } errorMessage)
                                    {
                                        markdownBuilder
                                            .Append("**")
                                            .Append(LocaleKey.ChatWindowViewModel_ExportMarkdown_ErrorMessage.I18N())
                                            .Append(":** ")
                                            .AppendLine(errorMessage)
                                            .AppendLine();
                                    }
                                }
                                break;
                            }
                            case AssistantChatMessageReasoningSpan { ReasoningOutput: { Length: > 0 } reasoningOutput }:
                            {
                                markdownBuilder
                                    .AppendLine("<details open>")
                                    .Append("<summary><b>")
                                    .Append("🤔 ")
                                    .Append(LocaleKey.ChatWindowViewModel_ExportMarkdown_ReasoningOutput.I18N())
                                    .AppendLine("</b></summary>")
                                    .AppendLine();

                                foreach (var line in reasoningOutput.Split(["\r\n", "\r", "\n"], StringSplitOptions.None))
                                {
                                    markdownBuilder.Append("> ").AppendLine(line);
                                }

                                markdownBuilder.AppendLine("</details>").AppendLine();
                                break;
                            }
                        }
                    }

                    markdownBuilder.AppendLine();
                    break;
                }
            }
        }

        var markdownContent = markdownBuilder.ToString();

        await using var writer = new StreamWriter(outputStream, Encoding.UTF8);
        await writer.WriteAsync(markdownContent.AsMemory(), cancellationToken);
    }
}