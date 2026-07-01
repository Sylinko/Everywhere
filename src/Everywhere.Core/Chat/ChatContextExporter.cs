using System.Text;
using ZLinq;

namespace Everywhere.Chat;

public static class ChatContextExporter
{
    public static async Task ExportAsMarkdown(ChatContext chatContext, Stream outputStream, CancellationToken cancellationToken = default)
    {
        var markdownBuilder = new StringBuilder();
        var metadata = chatContext.Metadata;

        markdownBuilder
            .Append("# ")
            .AppendLine(metadata.Topic ?? LocaleResolver.ChatContext_Metadata_Topic_Default)
            .AppendLine();

        markdownBuilder
            .Append("**")
            .Append(LocaleResolver.ChatWindowViewModel_ExportMarkdown_DateCreated)
            .Append(":** ")
            .AppendLine(metadata.DateCreated.ToString("F"))
            .AppendLine();

        markdownBuilder
            .Append("**")
            .Append(LocaleResolver.ChatWindowViewModel_ExportMarkdown_DateModified)
            .Append(":** ")
            .AppendLine(metadata.DateModified.ToString("F"))
            .AppendLine();

        markdownBuilder.AppendLine("---").AppendLine();

        foreach (var chatMessage in chatContext
                     .Items
                     .AsValueEnumerable()
                     .Select(node => node.Message))
        {
            switch (chatMessage)
            {
                case UserChatMessage user:
                {
                    markdownBuilder
                        .Append("## 👤 ")
                        .AppendLine(LocaleResolver.ChatWindowViewModel_ExportMarkdown_UserRole)
                        .AppendLine()
                        .AppendLine(user.Content)
                        .AppendLine();

                    if (user.Attachments.Any())
                    {
                        markdownBuilder
                            .Append("**")
                            .Append(LocaleResolver.ChatWindowViewModel_ExportMarkdown_UserAttachments)
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
                        .AppendLine(LocaleResolver.ChatWindowViewModel_ExportMarkdown_AssistantRole)
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
                                        .Append(LocaleResolver.ChatWindowViewModel_ExportMarkdown_FunctionCall)
                                        .Append(":** ")
                                        .AppendLine(functionCall.HeaderKey?.ToString())
                                        .AppendLine();

                                    foreach (var result in functionCall.Results
                                                 .AsValueEnumerable()
                                                 .Select(r => r.Result?.ToString())
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
                                            .Append(LocaleResolver.ChatWindowViewModel_ExportMarkdown_ErrorMessage)
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
                                    .Append(LocaleResolver.ChatWindowViewModel_ExportMarkdown_ReasoningOutput)
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
        await writer.WriteAsync(markdownContent);
    }
}