namespace Everywhere.Messages;

/// <summary>
/// Requests navigation in the main window.
/// </summary>
/// <remarks>
/// String routes intentionally behave like browser paths. The first path segments
/// match main navigation items by route key; remaining segments are passed to the
/// target view model through <c>ReactiveViewModelBase.OnNavigatedTo</c>.
/// </remarks>
public sealed record MainViewNavigateMessage(object Route)
{
    /// <summary>
    /// Route key for the home page. Kept as the class-name key for compatibility.
    /// </summary>
    public const string HomePageRoute = "HomePage";

    /// <summary>
    /// Route key for the custom assistant page. Kept as the class-name key for compatibility.
    /// </summary>
    public const string CustomAssistantPageRoute = "CustomAssistantPage";

    /// <summary>
    /// Route key for the chat plugin page. Kept as the class-name key for compatibility.
    /// </summary>
    public const string ChatPluginPageRoute = "ChatPluginPage";

    /// <summary>
    /// Route key reserved for the future Prompt Manager page.
    /// </summary>
    public const string PromptPageRoute = "PromptPage";

    /// <summary>
    /// Route key for the skill page. Kept as the class-name key for compatibility.
    /// </summary>
    public const string SkillPageRoute = "SkillPage";

    /// <summary>
    /// Builds a route that opens the custom assistant page and selects the assistant.
    /// </summary>
    public static string ToCustomAssistant(Guid assistantId) =>
        $"{CustomAssistantPageRoute}/{Uri.EscapeDataString(assistantId.ToString("D"))}";

    /// <summary>
    /// Builds a route that opens the future Prompt Manager page and selects the prompt.
    /// </summary>
    public static string ToPrompt(Guid promptId) =>
        $"{PromptPageRoute}/{Uri.EscapeDataString(promptId.ToString("D"))}";
}