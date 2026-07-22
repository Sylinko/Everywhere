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
        Assert.That(
            ToolSettingsKey.ForFunction("plugin/~", "function/name"),
            Is.EqualTo("function:plugin~1~0/function~1name"));
    }

    [Test]
    public void ToolEnablementSettings_WhenSerialized_RoundTripsExactOverrides()
    {
        var source = new ToolEnablementSettings
        {
            [ToolSettingsKey.ForPlugin("builtin.web")] = true,
            [ToolSettingsKey.ForFunction("builtin.web", "web_search")] = false
        };

        var json = JsonSerializer.Serialize(source);
        var result = JsonSerializer.Deserialize<ToolEnablementSettings>(json);

        Assert.That(result, Is.EqualTo(source));
    }

    [Test]
    public void ToolRulesets_WhenPatternsOverlap_MoreSpecificRulesWin()
    {
        using var plugin = new TestPlugin("web", isDefaultEnabled: false, "web_search", "web_extract");
        var rulesets = new ToolRulesets
        {
            ["builtin.*"] = new ToolFunctionRulesets
            {
                ["*"] = true,
                ["web*"] = false
            },
            ["builtin.web"] = new ToolFunctionRulesets
            {
                ["web_search"] = true
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(rulesets.IsPluginAllowed(plugin), Is.True);
            Assert.That(rulesets.IsFunctionAllowed(plugin, plugin.GetChatFunctions()[0]), Is.True);
            Assert.That(rulesets.IsFunctionAllowed(plugin, plugin.GetChatFunctions()[1]), Is.False);
        });
    }

    [Test]
    public void ToolRulesets_WhenMessagePackSerialized_RoundTripsNestedRules()
    {
        var source = new ToolRulesets
        {
            ["builtin.*"] = new ToolFunctionRulesets
            {
                ["*"] = true,
                ["web*"] = false
            }
        };

        var bytes = MessagePackSerializer.Serialize(source);
        var result = MessagePackSerializer.Deserialize<ToolRulesets>(bytes);

        Assert.That(result["builtin.*"], Is.EqualTo(source["builtin.*"]));
    }

    [Test]
    public void ToolRulesets_WhenDeserializingLegacyMessagePack_ConvertsFlatRules()
    {
        var legacy = new Dictionary<string, bool>
        {
            ["builtin.web_browser.*"] = true,
            ["builtin.web_browser.web_search"] = false,
            ["builtin.web.web_search"] = false,
            ["mcp.server.foo.bar"] = true
        };

        var bytes = MessagePackSerializer.Serialize(legacy);
        var result = MessagePackSerializer.Deserialize<ToolRulesets>(bytes);

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
        using var context = new ToolSettingsContext(new ToolEnablementSettings(), new ToolAutoApprovalSettings());
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
        using var context = new ToolSettingsContext(new ToolEnablementSettings(), new ToolAutoApprovalSettings());
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
        using var context = new ToolSettingsContext(new ToolEnablementSettings(), new ToolAutoApprovalSettings());
        using var presentation = new ToolPluginPresentation(plugin, context);

        presentation.Functions[0].IsEnabled = false;
        presentation.IsEnabled = false;

        Assert.Multiple(() =>
        {
            Assert.That(presentation.IsEnabled, Is.False);
            Assert.That(presentation.Functions[0].IsEnabled, Is.False);
            Assert.That(presentation.Functions[1].IsEnabled, Is.True);
            Assert.That(presentation.Functions.All(static function => function.CanEdit), Is.True);
            Assert.That(presentation.Functions.All(static function => function.CanEditAutoApproval), Is.True);
        });

        presentation.Functions[1].IsEnabled = false;
        Assert.That(presentation.Functions[1].IsEnabled, Is.False);
    }

    [Test]
    public void AssistantContext_WhenFollowingGlobal_UsesGlobalValuesAndCannotEdit()
    {
        using var plugin = new TestPlugin("test", isDefaultEnabled: false, "function");
        var global = new ToolEnablementSettings
        {
            [ToolSettingsKey.ForPlugin(plugin)] = true
        };
        using var context = new ToolSettingsContext(global, assistantOverrides: null);

        Assert.Multiple(() =>
        {
            Assert.That(context.IsFollowingGlobal, Is.True);
            Assert.That(context.CanEdit, Is.False);
            Assert.That(context.GetPluginEnabled(plugin), Is.True);
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
                    ChatFunctionPermissions.None,
                    functionName: name))
                .ToArray();
            _functionsSource.Edit(list => list.AddRange(functions));
        }
    }
}