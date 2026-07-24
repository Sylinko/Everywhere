using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Everywhere.AI;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.Views;

/// <summary>
/// Presents the tool settings owned by one custom assistant.
/// </summary>
public class CustomAssistantToolSettingsView(IChatPluginManager manager, Settings settings) : TemplatedControl
{
    public static readonly StyledProperty<CustomAssistant?> AssistantProperty =
        AvaloniaProperty.Register<CustomAssistantToolSettingsView, CustomAssistant?>(nameof(Assistant));

    public static readonly DirectProperty<CustomAssistantToolSettingsView, ToolSettingsPresentation?> ToolSettingsProperty =
        AvaloniaProperty.RegisterDirect<CustomAssistantToolSettingsView, ToolSettingsPresentation?>(
            nameof(ToolSettings),
            control => control.ToolSettings);

    public static readonly DirectProperty<CustomAssistantToolSettingsView, bool> IsFollowingGlobalProperty =
        AvaloniaProperty.RegisterDirect<CustomAssistantToolSettingsView, bool>(
            nameof(IsFollowingGlobal),
            control => control.IsFollowingGlobal,
            (control, value) => control.IsFollowingGlobal = value);

    public CustomAssistant? Assistant
    {
        get => GetValue(AssistantProperty);
        set => SetValue(AssistantProperty, value);
    }

    public ToolSettingsPresentation? ToolSettings
    {
        get;
        private set => SetAndRaise(ToolSettingsProperty, ref field, value);
    }

    public bool IsFollowingGlobal
    {
        get => Assistant?.ToolEnablementRulesets is null;
        set
        {
            var assistant = Assistant;
            if (assistant is null || value == IsFollowingGlobal) return;

            assistant.ToolEnablementRulesets = value ? null : new ObservableToolRulesets();
        }
    }

    public CustomAssistantToolSettingsView() : this(
        ServiceLocator.Resolve<IChatPluginManager>(),
        ServiceLocator.Resolve<Settings>()) { }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AssistantProperty && VisualRoot is not null)
        {
            RebuildPresentation(change.OldValue as CustomAssistant, change.NewValue as CustomAssistant);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RebuildPresentation(null, Assistant);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleasePresentation(Assistant);
        base.OnDetachedFromVisualTree(e);
    }

    private void RebuildPresentation(CustomAssistant? oldAssistant, CustomAssistant? newAssistant)
    {
        ReleasePresentation(oldAssistant);
        if (newAssistant is null) return;

        newAssistant.PropertyChanged += HandleAssistantPropertyChanged;
        ToolSettings = new ToolSettingsPresentation(
            manager,
            ToolSettingsContext.CreateAssistant(settings.Plugin.ToolEnablementRulesets, newAssistant.ToolEnablementRulesets));
        RaisePropertyChanged(
            IsFollowingGlobalProperty,
            oldAssistant?.ToolEnablementRulesets is null,
            newAssistant.ToolEnablementRulesets is null);
    }

    private void ReleasePresentation(CustomAssistant? assistant)
    {
        if (assistant is not null) assistant.PropertyChanged -= HandleAssistantPropertyChanged;
        ToolSettings?.Dispose();
        ToolSettings = null;
    }

    private void HandleAssistantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CustomAssistant.ToolEnablementRulesets)) return;

        ToolSettings?.Context.SetAssistantOverrides(Assistant?.ToolEnablementRulesets);
        RaisePropertyChanged(IsFollowingGlobalProperty, !IsFollowingGlobal, IsFollowingGlobal);
    }
}