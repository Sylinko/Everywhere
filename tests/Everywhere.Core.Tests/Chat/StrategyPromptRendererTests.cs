using Everywhere.Chat;
using Everywhere.StrategyEngine;

namespace Everywhere.Core.Tests.Chat;

public class StrategyPromptRendererTests
{
    [Test]
    public void RenderUserPrompt_PreprocessorVariablesOverridePromptVariables()
    {
        var result = StrategyPromptRenderer.RenderUserPrompt(
            "Selected: {preprocess.selection.text}",
            null,
            new PreprocessorResult
            {
                Variables = new Dictionary<string, string>
                {
                    ["preprocess.selection.text"] = "from preprocessor"
                }
            },
            new Dictionary<string, Func<string>>
            {
                ["preprocess.selection.text"] = () => "from prompt"
            });

        Assert.That(result, Is.EqualTo("Selected: from preprocessor"));
    }

    [Test]
    public void RenderUserPrompt_ArgumentPlaceholder_DoesNotAppendUserInputTwice()
    {
        var result = StrategyPromptRenderer.RenderUserPrompt(
            "Do: {Argument}",
            "summarize this",
            null,
            new Dictionary<string, Func<string>>());

        Assert.That(result, Is.EqualTo("Do: summarize this"));
    }

    [Test]
    public void RenderUserPrompt_WithoutArgumentPlaceholder_AppendsUserInputSection()
    {
        var result = StrategyPromptRenderer.RenderUserPrompt(
            "Do the thing.",
            "extra request",
            null,
            new Dictionary<string, Func<string>>());

        Assert.That(result, Is.EqualTo("Do the thing.\r\n<UserRequestStart>\r\nextra request")
            .Or.EqualTo("Do the thing.\n<UserRequestStart>\nextra request"));
    }

    [Test]
    public void RenderSystemPrompt_UsesPreprocessorVariables()
    {
        var result = StrategyPromptRenderer.RenderSystemPrompt(
            "System sees {preprocess.browser.title} on {Date}",
            new Dictionary<string, Func<string>>
            {
                ["Date"] = () => "Monday"
            },
            new PreprocessorResult
            {
                Variables = new Dictionary<string, string>
                {
                    ["preprocess.browser.title"] = "Docs"
                }
            });

        Assert.That(result, Is.EqualTo("System sees Docs on Monday"));
    }
}
