using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.TextSelection;

/// <summary>
/// Extension methods for registering text-selection services.
/// </summary>
public static class TextSelectionServiceCollectionExtensions
{
    /// <summary>
    /// Adds text-selection monitoring and toolbar services to the service collection.
    /// </summary>
    public static IServiceCollection AddTextSelection(this IServiceCollection services) =>
        services
            .AddSingleton<TextSelectionController>()
            .AddSingleton<ITextSelectionWatcher>(sp => new AutomationTextSelectionWatcher(
                sp.GetRequiredService<IHostConnectionSource>(),
                sp.GetRequiredService<ChatVisualService>(),
                sp.GetRequiredService<Settings>(),
                sp.GetRequiredService<ILogger<AutomationTextSelectionWatcher>>()))
            .AddSingleton<TextSelectionToolbarActionsManager>()
            .AddSingleton<TextSelectionToolbarService>();
}