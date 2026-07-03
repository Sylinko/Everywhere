using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Everywhere.Common;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;
using ZLinq;

namespace Everywhere.Extensions;

public static class AvaloniaExtensions
{
    public static AnonymousExceptionHandler ToExceptionHandler(this DialogManager dialogManager) => new((exception, message, source, lineNumber) =>
        dialogManager.CreateDialog(exception.GetFriendlyMessage().ToString() ?? "Unknown error", $"[{source}:{lineNumber}] {message ?? "Error"}"));

    public static AnonymousExceptionHandler ToExceptionHandler(this ToastHost toastHost) => new((exception, message, source, lineNumber) =>
        toastHost.CreateToast($"[{source}:{lineNumber}] {message ?? "Error"}")
            .WithContent(exception.GetFriendlyMessage().ToTextBlock())
            .DismissOnClick()
            .ShowError());

    public static TextBlock ToTextBlock(this IDynamicLocaleKey dynamicResourceKey)
    {
        return new TextBlock
        {
            Classes = { nameof(DynamicLocaleKey) },
            [!TextBlock.TextProperty] = dynamicResourceKey.ToBinding()
        };
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection AddDialogManagerAndToastManager()
        {
            return services
                .AddTransient<DialogManager>(_ => TryGetReactiveHost()?.DialogHost.Manager ?? new DialogManager())
                .AddTransient<ToastHost>(_ => TryGetReactiveHost()?.ToastHost ?? new ToastHost());

            IReactiveHost? TryGetReactiveHost()
            {
                if (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime is not { } lifetime) return null;

                return lifetime.Windows.AsValueEnumerable().FirstOrDefault(w => w.IsActive) as IReactiveHost ??
                    lifetime.MainWindow as IReactiveHost ??
                    lifetime.Windows.AsValueEnumerable().OfType<IReactiveHost>().FirstOrDefault();
            }
        }
    }
}