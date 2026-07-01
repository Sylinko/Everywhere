using Everywhere.AI;
using Everywhere.Chat;

namespace Everywhere.Core.Tests.AI;

/// <summary>
/// Tests for <see cref="PromptTemplateRenderer.Render"/>, the recursive prompt-variable resolver.
/// Covers issue #291: a custom system prompt can include the built-in default via the
/// {DefaultSystemPrompt} variable, which is resolved recursively so the placeholders inside the
/// default prompt ({OS}/{Date}/...) are also expanded — with infinite-recursion protection.
/// </summary>
public class PromptTemplateRendererTests
{
    private static Func<string, string?> Resolver(Dictionary<string, string?> map) =>
        key => map.TryGetValue(key, out var value) ? value : null;

    private static Dictionary<string, string?> Map(params (string Key, string? Value)[] entries)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries) dict[key] = value;
        return dict;
    }

    // Resolver mimicking ChatContext.GetPromptVariables() + the new DefaultSystemPrompt variable.
    private static Func<string, string?> DefaultPromptResolver() => Resolver(Map(
        ("DefaultSystemPrompt", Prompts.DefaultSystemPrompt),
        ("OS", "Windows"),
        ("Date", "Monday"),
        ("SystemLanguage", "English"),
        ("WorkingDirectory", "C:/wd")));

    private static string ResolvedDefault() => Prompts.DefaultSystemPrompt
        .Replace("{OS}", "Windows")
        .Replace("{Date}", "Monday")
        .Replace("{SystemLanguage}", "English")
        .Replace("{WorkingDirectory}", "C:/wd");

    [Test]
    public void Render_LeafVariable_ResolvesSinglePass()
    {
        var result = PromptTemplateRenderer.Render("{Date}", Resolver(Map(("Date", "Monday"))));
        Assert.That(result, Is.EqualTo("Monday"));
    }

    [Test]
    public void Render_UnknownVariable_LeftLiteral()
    {
        var result = PromptTemplateRenderer.Render("{Foo}", _ => null);
        Assert.That(result, Is.EqualTo("{Foo}"));
    }

    [Test]
    public void Render_EscapedDoubleBrace_StaysLiteral()
    {
        var result = PromptTemplateRenderer.Render("{{OS}}", Resolver(Map(("OS", "Windows"))));
        Assert.That(result, Is.EqualTo("{{OS}}"));
    }

    [Test]
    public void Render_TripleBrace_StaysLiteral()
    {
        var result = PromptTemplateRenderer.Render("{{{OS}}}", Resolver(Map(("OS", "Windows"))));
        Assert.That(result, Is.EqualTo("{{{OS}}}"));
    }

    [Test]
    public void Render_EscapedNextToReal_OnlyRealResolves()
    {
        var result = PromptTemplateRenderer.Render("{{OS}} {OS}", Resolver(Map(("OS", "Windows"))));
        Assert.That(result, Is.EqualTo("{{OS}} Windows"));
    }

    [Test]
    public void Render_DefaultSystemPrompt_ExpandsNestedLeafVariables()
    {
        var result = PromptTemplateRenderer.Render("{DefaultSystemPrompt}", DefaultPromptResolver());
        Assert.That(result, Is.EqualTo(ResolvedDefault()));
        Assert.That(result, Does.Not.Contain("{OS}"));
        Assert.That(result, Does.Not.Contain("{Date}"));
    }

    [Test]
    public void Render_CustomPromptWithDefaultPlaceholder_AppendsResolvedDefault()
    {
        var result = PromptTemplateRenderer.Render(
            "Rules.\n\n{DefaultSystemPrompt}\n\nExtra.",
            DefaultPromptResolver());
        Assert.That(result, Is.EqualTo("Rules.\n\n" + ResolvedDefault() + "\n\nExtra."));
    }

    [Test]
    public void Render_DefaultSystemPromptTwice_BothFullyExpand()
    {
        var result = PromptTemplateRenderer.Render("{DefaultSystemPrompt}|{DefaultSystemPrompt}", DefaultPromptResolver());
        Assert.That(result, Is.EqualTo(ResolvedDefault() + "|" + ResolvedDefault()));
    }

    [Test]
    public void Render_SelfReferentialValue_BreaksWithoutHang()
    {
        var result = PromptTemplateRenderer.Render("{A}", Resolver(Map(("A", "loop {A} end"))));
        Assert.That(result, Is.EqualTo("loop {A} end"));
    }

    [Test]
    public void Render_MutualCycle_TerminatesLeavingLiteral()
    {
        var result = PromptTemplateRenderer.Render("{A}", Resolver(Map(("A", "a={B}"), ("B", "b={A}"))));
        Assert.That(result, Is.EqualTo("a=b={A}"));
    }

    [Test]
    public void Render_RepeatedNonNestedVariable_ResolvesEachTime()
    {
        var result = PromptTemplateRenderer.Render("{X} and {X}", Resolver(Map(("X", "v"))));
        Assert.That(result, Is.EqualTo("v and v"));
    }

    [Test]
    public void Render_DepthCapExceeded_StopsGracefully()
    {
        // A finite-but-too-deep, non-cyclic chain V0 -> {V1} -> {V2} -> ... longer than MaxDepth.
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        var chainLength = PromptTemplateRenderer.MaxDepth + 5;
        for (var i = 0; i <= chainLength; i++) map["V" + i] = "{V" + (i + 1) + "}";

        string result = string.Empty;
        Assert.That(() => result = PromptTemplateRenderer.Render("{V0}", Resolver(map)), Throws.Nothing);
        // Degrades gracefully: stops at the cap leaving an unresolved placeholder rather than looping.
        Assert.That(result, Does.Contain("{"));
    }

    [Test]
    public void Render_TitleResolver_DoesNotLeakUnknownVariables()
    {
        // The title generation resolver only knows {UserMessage}/{SystemLanguage}; a user-typed {Date}
        // inside the message must stay literal even under recursion (returns null -> left as-is).
        var resolver = Resolver(Map(("UserMessage", "hi {Date}"), ("SystemLanguage", "English")));
        var result = PromptTemplateRenderer.Render(Prompts.TitleGeneratorUserPrompt, resolver);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("hi {Date}"));   // inner unknown placeholder preserved
            Assert.That(result, Does.Contain("English"));
            Assert.That(result, Does.Not.Contain("{UserMessage}"));
            Assert.That(result, Does.Not.Contain("{SystemLanguage}"));
        });
    }

    [Test]
    public void Render_ResolvedValueWithKnownPlaceholder_ExpandsRecursively()
    {
        // Documents the deliberate behavior delta: when a resolved value itself contains a placeholder
        // the resolver knows, recursion expands it (e.g. a strategy {Argument} containing {OS}).
        var resolver = Resolver(Map(("Argument", "open {OS} now"), ("OS", "Windows")));
        var result = PromptTemplateRenderer.Render("Do {Argument}", resolver);
        Assert.That(result, Is.EqualTo("Do open Windows now"));
    }

    [Test]
    public void Render_UserTextWithKnownAndUnknownPlaceholders_ExpandsOnlyKnown()
    {
        // Pins the user-input recursion contract on a title-style resolver that knows {SystemLanguage}
        // but not {OS}: a known inner placeholder expands; an unknown one is left literal.
        var resolver = Resolver(Map(("UserMessage", "lang {SystemLanguage}, os {OS}"), ("SystemLanguage", "English")));
        var result = PromptTemplateRenderer.Render("{UserMessage}", resolver);
        Assert.That(result, Is.EqualTo("lang English, os {OS}"));
    }
}
