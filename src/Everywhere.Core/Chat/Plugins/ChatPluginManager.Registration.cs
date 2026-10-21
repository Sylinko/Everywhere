using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Plugins;

public partial class ChatPluginManager
{
    // Keep closed entries while a discovery reader still holds the plugin. Such readers must not
    // accidentally treat a retired contribution as a permanent built-in tool.
    private readonly ConditionalWeakTable<ModChatPlugin, ChatPluginRegistration> _registrations = new();

    /// <inheritdoc />
    public IAsyncDisposable RegisterChatPlugin(ModChatPlugin plugin)
    {
        var registration = new ChatPluginRegistration(plugin, this);
        _nativePluginsSource.Edit(list =>
        {
            if (list.AsValueEnumerable().Any(existing => existing.Key == plugin.Key))
            {
                throw new InvalidOperationException($"Chat plugin '{plugin.Key}' is already registered.");
            }

            _registrations.Add(plugin, registration);
            list.Add(plugin);
        });

        return registration;
    }

    /// <summary>
    /// Calls the factory added by the Semantic Kernel build-time patch. Compilation uses the original SK reference.
    /// </summary>
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "CreateRevocableProxy")]
    private static extern KernelFunction CreateRevocableProxy(
        KernelFunction? declaringType,
        KernelFunction target,
        Func<IDisposable> enterInvocation);

    /// <summary>
    /// Owns a tool activation. Discovery exposes the real plugin; chat scopes get weak proxies.
    /// Closing admission drains discovery, execution, filters and streaming before plugin resources are released.
    /// </summary>
    private sealed class ChatPluginRegistration(ModChatPlugin plugin, ChatPluginManager manager) : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        // Ownership is intentional: dynamically discovered functions must survive until this activation closes.
        // ReSharper disable once CollectionNeverQueried.Local
        private readonly HashSet<NativeChatFunction> _functions = [];
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ModChatPlugin? _plugin = plugin;
        private bool _isClosed;
        private int _activeCalls;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_isClosed) return new ValueTask(_drained.Task);

                _isClosed = true;
                var plugin = _plugin;
                _plugin = null;
                _functions.Clear();

                try
                {
                    // DI may dispose discovery before the mod entry during normal application exit.
                    if (!manager._isDisposed && plugin is not null) manager._nativePluginsSource.Remove(plugin);
                }
                finally
                {
                    if (_activeCalls == 0) _drained.TrySetResult();
                }

                return new ValueTask(_drained.Task);
            }
        }

        /// <summary>
        /// Keeps the original function available for this activation, including functions returned dynamically.
        /// Snapshot callbacks only retain a weak reference to that function.
        /// </summary>
        public NativeChatFunction CreateFunction(NativeChatFunction source)
        {
            lock (_gate)
            {
                if (_isClosed) throw new InvalidOperationException("This chat plugin has been unloaded.");

                _functions.Add(source);
                var weakSource = new WeakReference<NativeChatFunction>(source);
                return new NativeChatFunction(
                    CreateRevocableProxy(null, source.KernelFunction, EnterInvocation),
                    source,
                    source.PermissionConsentHook is null ? null : InspectConsent,
                    source.BeforeCallHook is null ? null : BeforeCall);

                // The closures retain only this registration and a weak source, never plugin delegates.
                bool? InspectConsent(FunctionCallContext context, PermissionConsentRequest request)
                {
                    lock (_gate)
                    {
                        return _isClosed || !weakSource.TryGetTarget(out var function) ?
                            false :
                            function.PermissionConsentHook?.Invoke(context, request);
                    }
                }

                ChatPluginDisplayBlock? BeforeCall(FunctionCallContent content)
                {
                    lock (_gate)
                    {
                        return _isClosed || !weakSource.TryGetTarget(out var function) ?
                            null :
                            function.BeforeCallHook?.Invoke(content);
                    }
                }
            }
        }

        /// <summary>
        /// Serializes admission with retirement; the returned lease covers the complete SK pipeline.
        /// </summary>
        internal IDisposable EnterInvocation()
        {
            lock (_gate)
            {
                if (_isClosed) throw new InvalidOperationException("This chat plugin has been unloaded.");

                _activeCalls++;
                return Disposable.Create(ReleaseInvocation);
            }
        }

        /// <summary>
        /// Completes retirement once every admitted operation has returned.
        /// </summary>
        private void ReleaseInvocation()
        {
            lock (_gate)
            {
                _activeCalls--;
                if (_isClosed && _activeCalls == 0) _drained.TrySetResult();
            }
        }
    }
}