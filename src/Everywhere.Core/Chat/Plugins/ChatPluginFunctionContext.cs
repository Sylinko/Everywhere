using Everywhere.AI;
using Everywhere.Chat;

namespace Everywhere.Chat.Plugins;

public sealed record ChatPluginFunctionContext(
    Assistant Assistant,
    ChatContext ChatContext,
    ToolRulesets? ToolRulesets,
    IServiceProvider ServiceProvider,
    CancellationToken CancellationToken)
{
    public bool SupportsToolCall => Assistant.SupportsToolCall;

    public Modalities InputModalities => Assistant.InputModalities;

    public Modalities OutputModalities => Assistant.OutputModalities;

    public bool SupportsInputImage => InputModalities.SupportsImage;

    public bool SupportsOutputImage => OutputModalities.SupportsImage;
}
