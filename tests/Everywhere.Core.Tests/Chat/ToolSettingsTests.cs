using System.Text.Json;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Everywhere.Views;
using MessagePack;

namespace Everywhere.Core.Tests.Chat;

public class ToolSettingsTests
{
    [Test]
    public void ToolSettingsKey_WithAmbiguousNames_ProducesDistinctKeys()
    {
        var first = ToolSettingsKey.ForFunction("plugin.part", "function");
        var second = ToolSettingsKey.ForFunction("plugin", "part.function");

        Assert.That(first, Is.Not.EqualTo(second));
        Assert.Multiple(() =>
        {
            Assert.That(ToolSettingsKey.ForPlugin("builtin.web"), Is.EqualTo("p:builtin.web"));
            Assert.That(
                ToolSettingsKey.ForFunction("plugin/~", "function/name"),
                Is.EqualTo("f:plugin~1~0/function~1name"));
        });
    }

    [Test]
    public void ObservableToolRulesets_WhenSerialized_RoundTripsExactRules()
    {
        var source = new ObservableToolRulesets
        {
            [ToolSettingsKey.ForPlugin("builtin.web")] = true,
            [ToolSettingsKey.ForFunction("builtin.web", "web_search")] = false
        };

        var json = JsonSerializer.Serialize(source);
        var result = JsonSerializer.Deserialize<ObservableToolRulesets>(json);

        Assert.That(result, Is.EqualTo(source));
    }

    [Test]
    public void ToolPatternRulesets_WhenPatternsOverlap_MoreSpecificRulesWin()
    {
        using var plugin = new TestPlugin("web", isDefaultEnabled: false, "web_search", "web_extract");
        var rulesets = new ToolPatternRulesets
        {
            ["builtin.*"] = new ToolFunctionPatternRulesets
            {
                ["*"] = true,
                ["web*"] = false
            },
            ["builtin.web"] = new ToolFunctionPatternRulesets
            {
                ["web_search"] = true
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(rulesets.GetPluginRule(plugin), Is.True);
            Assert.That(rulesets.GetFunctionRule(plugin, plugin.GetChatFunctions()[0]), Is.True);
            Assert.That(rulesets.GetFunctionRule(plugin, plugin.GetChatFunctions()[1]), Is.False);
        });
    }

    [Test]
    public void ToolPatternRulesets_WhenMessagePackSerialized_RoundTripsNestedRules()
    {
        var source = new ToolPatternRulesets
        {
            ["builtin.*"] = new ToolFunctionPatternRulesets
            {
                ["*"] = true,
                ["web*"] = false
            }
        };

        var bytes = MessagePackSerializer.Serialize(source);
        var result = MessagePackSerializer.Deserialize<ToolPatternRulesets>(bytes);

        Assert.That(result["builtin.*"], Is.EqualTo(source["builtin.*"]));
    }

    [Test]
    public void ToolPatternRulesets_WhenDeserializingLegacyMessagePack_ConvertsFlatRules()
    {
        var legacy = new Dictionary<string, bool>
        {
            ["builtin.web_browser.*"] = true,
            ["builtin.web_browser.web_search"] = false,
            ["builtin.web.web_search"] = false,
            ["mcp.server.foo.bar"] = true
        };

        var bytes = MessagePackSerializer.Serialize(legacy);
        var result = MessagePackSerializer.Deserialize<ToolPatternRulesets>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(result["builtin.web_browser"]["*"], Is.True);
            Assert.That(result["builtin.web_browser"]["web_search"], Is.False);
            Assert.That(result["builtin.web"]["web_search"], Is.False);
            Assert.That(result["mcp.server"]["foo.bar"], Is.True);
        });
    }

    [Test]
    public void SetFunctionEnabled_DoesNotChangePluginEnabledState()
    {
        using var plugin = new TestPlugin("test", isDefaultEnabled: true, "first", "second");
        using var context = ToolSettingsContext.CreateGlobal(new ObservableToolRulesets(), new ObservableToolRulesets());
        var functions = plugin.GetChatFunctions();

        context.SetFunctionEnabled(plugin, functions[0], false);
        Assert.Multiple(() =>
        {
            Assert.That(context.GetPluginEnabled(plugin), Is.True);
            Assert.That(context.GetFunctionEnabled(plugin, functions[0]), Is.False);
        });

        context.SetPluginEnabled(plugin, false);
        context.SetFunctionEnabled(plugin, functions[1], true);

        Assert.Multiple(() =>
        {
            Assert.That(context.GetPluginEnabled(plugin), Is.False);
            Assert.That(context.GetFunctionEnabled(plugin, functions[1]), Is.True);
        });
    }

    [Test]
    public void SetPluginEnabled_DoesNotChangeFunctionValues()
    {
        using var plugin = new TestPlugin("test", isDefaultEnabled: true, "first", "second");
        using var context = ToolSettingsContext.CreateGlobal(new ObservableToolRulesets(), new ObservableToolRulesets());
        var functions = plugin.GetChatFunctions();

        context.SetFunctionEnabled(plugin, functions[0], false);
        context.SetPluginEnabled(plugin, false);

        Assert.Multiple(() =>
        {
            Assert.That(context.GetPluginEnabled(plugin), Is.False);
            Assert.That(context.GetFunctionEnabled(plugin, functions[0]), Is.False);
            Assert.That(context.GetFunctionEnabled(plugin, functions[1]), Is.True);
        });

        context.SetPluginEnabled(plugin, true);

        Assert.Multiple(() =>
        {
            Assert.That(context.GetPluginEnabled(plugin), Is.True);
            Assert.That(context.GetFunctionEnabled(plugin, functions[0]), Is.False);
            Assert.That(context.GetFunctionEnabled(plugin, functions[1]), Is.True);
        });
    }

    [Test]
    public void DisabledPlugin_PreservesFunctionValuesAndAllowsEditing()
    {
        using var plugin = new TestPlugin("test", isDefaultEnabled: true, "first", "second");
        using var context = ToolSettingsContext.CreateGlobal(new ObservableToolRulesets(), new ObservableToolRulesets());
        using var presentation = new ToolPluginPresentation(plugin, context);

        presentation.Functions[0].IsEnabled = false;
        presentation.IsEnabled = false;

        Assert.Multiple(() =>
        {
            Assert.That(presentation.IsEnabled, Is.False);
            Assert.That(presentation.Functions[0].IsEnabled, Is.False);
            Assert.That(presentation.Functions[1].IsEnabled, Is.True);
            Assert.That(presentation.Functions.All(static function => function.CanEdit), Is.True);
            Assert.That(presentation.Functions.All(static function => function.CanEditBypassesApproval), Is.True);
        });

        presentation.Functions[1].IsEnabled = false;
        Assert.That(presentation.Functions[1].IsEnabled, Is.False);
    }

    [Test]
    public void AssistantContext_WhenFollowingGlobal_UsesGlobalValuesAndCannotEdit()
    {
        using var plugin = new TestPlugin("test", isDefaultEnabled: false, "function");
        var global = new ObservableToolRulesets
        {
            [ToolSettingsKey.ForPlugin(plugin)] = true
        };
        using var context = ToolSettingsContext.CreateAssistant(global, assistantOverrides: null);

        Assert.Multiple(() =>
        {
            Assert.That(context.IsFollowingGlobal, Is.True);
            Assert.That(context.CanEdit, Is.False);
            Assert.That(context.GetPluginEnabled(plugin), Is.True);
        });
    }

    [Test]
    public void BypassApproval_WhenFunctionOverridesPlugin_UsesFunctionRule()
    {
        using var plugin = new TestPlugin("test", isDefaultEnabled: true, "first", "second");
        var rulesets = new ObservableToolRulesets
        {
            [ToolSettingsKey.ForPlugin(plugin)] = true,
            [ToolSettingsKey.ForFunction(plugin, plugin.GetChatFunctions()[0])] = false
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                ToolBypassApprovalPolicy.BypassesApproval(rulesets, plugin, plugin.GetChatFunctions()[0]),
                Is.False);
            Assert.That(
                ToolBypassApprovalPolicy.BypassesApproval(rulesets, plugin, plugin.GetChatFunctions()[1]),
                Is.True);
        });
    }

    [Test]
    public void SetPluginBypassApproval_WhenFunctionRequiresApproval_ClearsFalseOverride()
    {
        using var plugin = new TestPlugin("test", isDefaultEnabled: true, "function");
        var rulesets = new ObservableToolRulesets
        {
            [ToolSettingsKey.ForFunction(plugin, plugin.GetChatFunctions()[0])] = false
        };

        ToolBypassApprovalPolicy.SetPluginRule(rulesets, plugin, true);

        Assert.Multiple(() =>
        {
            Assert.That(rulesets.GetPluginRule(plugin), Is.True);
            Assert.That(rulesets.GetFunctionRule(plugin, plugin.GetChatFunctions()[0]), Is.Null);
        });
    }

    private sealed class TestPlugin : BuiltInChatPlugin
    {
        public override IDynamicLocaleKey HeaderKey { get; } = new DirectLocaleKey("Test");

        public override IDynamicLocaleKey DescriptionKey { get; } = new DirectLocaleKey("Test");

        public override bool IsDefaultEnabled { get; }

        public TestPlugin(string name, bool isDefaultEnabled, params string[] functionNames) : base(name)
        {
            IsDefaultEnabled = isDefaultEnabled;
            var functions = functionNames.Select(name => new BuiltInChatFunction(
                    (Func<string>)(static () => string.Empty),
                    ChatFunctionPermissions.AllAccess,
                    functionName: name))
                .ToArray();
            _functionsSource.Edit(list => list.AddRange(functions));
        }
    }
}
