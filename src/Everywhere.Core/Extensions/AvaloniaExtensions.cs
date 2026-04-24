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

    public static AnonymousExceptionHandler ToExceptionHandler(this ToastManager toastManager) => new((exception, message, source, lineNumber) =>
        toastManager.CreateToast($"[{source}:{lineNumber}] {message ?? "Error"}")
            .WithContent(exception.GetFriendlyMessage().ToTextBlock())
            .DismissOnClick()
            .ShowError());

    public static TextBlock ToTextBlock(this IDynamicResourceKey dynamicResourceKey)
    {
        return new TextBlock
        {
            Classes = { nameof(DynamicResourceKey) },
            [!TextBlock.TextProperty] = dynamicResourceKey.ToBinding()
        };
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection AddDialogManagerAndToastManager()
        {
            return services
                .AddTransient<DialogManager>(_ => TryGetReactiveHost()?.DialogHost.Manager ?? new DialogManager())
                .AddTransient<ToastManager>(_ => TryGetReactiveHost()?.ToastHost.Manager ?? new ToastManager());

            IReactiveHost? TryGetReactiveHost()
            {
                if (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime is not { } lifetime) return null;

                return lifetime.Windows.AsValueEnumerable().FirstOrDefault(w => w.IsActive) as IReactiveHost ??
                    lifetime.MainWindow as IReactiveHost ??
                    lifetime.Windows.AsValueEnumerable().OfType<IReactiveHost>().FirstOrDefault();
            }
        }

        public IServiceCollection AddDialogAndToastExceptionHandler()
        {
            return services
                .AddKeyedSingleton<IExceptionHandler>(
                    typeof(DialogManager),
                    (provider, _) => new DelegateExceptionHandler(() => provider.GetRequiredService<DialogManager>().ToExceptionHandler()))
                .AddKeyedSingleton<IExceptionHandler>(
                    typeof(ToastManager),
                    (provider, _) => new DelegateExceptionHandler(() => provider.GetRequiredService<ToastManager>().ToExceptionHandler()));
        }
    }

    private sealed class DelegateExceptionHandler(Func<IExceptionHandler> handlerFactory) : IExceptionHandler
    {
        public void HandleException(Exception exception, string? message = null, object? source = null, int lineNumber = 0)
        {
            // ReSharper disable once ExplicitCallerInfoArgument
            handlerFactory().HandleException(exception, message, source, lineNumber);
        }
    }
}