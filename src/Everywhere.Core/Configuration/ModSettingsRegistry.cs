using Avalonia.Controls;
using Avalonia.Threading;
using Everywhere.Modding.Abstractions;
using Everywhere.Views;

namespace Everywhere.Configuration;

/// <summary>
/// Owns lazy settings contributions. Registration can originate from mod workers; view mutation is UI-affine.
/// </summary>
public sealed class ModSettingsRegistry : IModSettingsRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IAsyncDisposable Register(IModContext context, Func<SettingsItems> createItems)
    {
        lock (_gate)
        {
            context.Stopping.ThrowIfCancellationRequested();
            var registration = new Registration(this, context, createItems);
            if (!_registrations.TryAdd(context.ModId, registration))
            {
                throw new InvalidOperationException($"Settings for mod '{context.ModId}' are already registered.");
            }

            return registration;
        }
    }

    /// <inheritdoc />
    public Control? CreateView(string modId)
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (_gate)
        {
            return _registrations.TryGetValue(modId, out var registration) ? registration.CreateView() : null;
        }
    }

    /// <summary>
    /// Serializes factory use and retirement through the registry gate. Closed handles retain no mod objects.
    /// </summary>
    private sealed class Registration(ModSettingsRegistry owner, IModContext context, Func<SettingsItems> createItems) : IAsyncDisposable
    {
        private readonly string _modId = context.ModId;
        private IModContext? _context = context;
        private Func<SettingsItems>? _createItems = createItems;
        private SettingsItemsControl? _view;
        private Task? _closure;

        /// <summary>
        /// Creates items only on actual UI demand and never after cooperative stopping begins.
        /// </summary>
        internal Control? CreateView()
        {
            if (_context is null || _context.Stopping.IsCancellationRequested || _createItems is null) return null;

            return _view ??= new SettingsItemsControl { ItemsSource = _createItems() };
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                if (_closure is not null) return new ValueTask(_closure);

                var isExiting = _context?.StopReason == ModStopReason.Exit;
                var view = _view;
                _view = null;
                _context = null;
                _createItems = null;
                owner._registrations.Remove(_modId);
                _closure = isExiting || view is null ? Task.CompletedTask : DetachAsync(view);
                return new ValueTask(_closure);
            }
        }

        /// <summary>
        /// Removes bindings from the host view before the mod's final settings flush can proceed.
        /// </summary>
        private static async Task DetachAsync(SettingsItemsControl view)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                view.ItemsSource = null;
                view.DataContext = null;
            });
        }
    }
}