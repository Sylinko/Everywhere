using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Everywhere.Chat.Permissions;
using Everywhere.Harness;
using Microsoft.Extensions.AI;

namespace Everywhere.Chat.Plugins;

public partial class ChatPluginManager
{
    // Keep closed entries while a discovery reader still holds the plugin so retired contributions
    // cannot be mistaken for permanent built-in tools.
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
    /// Owns a mod tool activation. Discovery exposes the real plugin; chat snapshots receive weak,
    /// revocable wrappers that hold no strong references to mod delegates or metadata after retirement.
    /// </summary>
    private sealed class ChatPluginRegistration(ModChatPlugin plugin, ChatPluginManager manager) : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        // Ownership is intentional: dynamically discovered functions must survive until this activation closes.
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
        /// Keeps the original function available for this activation while exposing only a weak,
        /// revocable <see cref="AIFunction"/> proxy and guarded hooks to the chat snapshot.
        /// </summary>
        public NativeChatFunction CreateFunction(NativeChatFunction source)
        {
            lock (_gate)
            {
                if (_isClosed) throw new InvalidOperationException("This chat plugin has been unloaded.");

                _functions.Add(source);
                var weakSource = new WeakReference<NativeChatFunction>(source);
                return new NativeChatFunction(
                    new RevocableAIFunction(source.Function, this, weakSource),
                    source,
                    source.BeforeCallHook is null ? null : BeforeCall,
                    source.PermissionConsentHook is null ? null : InspectConsent);

                // The closures retain only this registration and a weak source, never mod delegates.
                ChatPluginDisplayBlock? BeforeCall(FunctionCallContent content)
                {
                    lock (_gate)
                    {
                        return _isClosed || !weakSource.TryGetTarget(out var function) ?
                            null :
                            function.BeforeCallHook?.Invoke(content);
                    }
                }

                bool? InspectConsent(FunctionCallContext context, PermissionConsentRequest request)
                {
                    lock (_gate)
                    {
                        return _isClosed || !weakSource.TryGetTarget(out var function) ?
                            false :
                            function.PermissionConsentHook?.Invoke(context, request);
                    }
                }
            }
        }

        /// <summary>
        /// Admits a discovery or invocation lease if this registration is still open.
        /// </summary>
        public bool TryEnterInvocation([NotNullWhen(true)] out IDisposable? lease)
        {
            lock (_gate)
            {
                if (_isClosed)
                {
                    lease = null;
                    return false;
                }

                _activeCalls++;
                lease = Disposable.Create(ReleaseInvocation);
                return true;
            }
        }

        private IDisposable EnterInvocation()
        {
            if (!TryEnterInvocation(out var lease))
            {
                throw new InvalidOperationException("This chat plugin has been unloaded.");
            }

            return lease;
        }

        private void ReleaseInvocation()
        {
            lock (_gate)
            {
                _activeCalls--;
                if (_isClosed && _activeCalls == 0) _drained.TrySetResult();
            }
        }

        /// <summary>
        /// Revocable <see cref="AIFunction"/> proxy that copies schema and identity eagerly and holds only
        /// a weak reference to the mod's <see cref="NativeChatFunction"/> so unloaded ALCs can collect.
        /// </summary>
        private sealed class RevocableAIFunction(
            AIFunction metadataSource,
            ChatPluginRegistration registration,
            WeakReference<NativeChatFunction> weakSource) : AIFunction
        {
            public override string Name { get; } = metadataSource.Name;

            public override string Description { get; } = metadataSource.Description;

            public override JsonElement JsonSchema { get; } = metadataSource.JsonSchema;

            public override JsonElement? ReturnJsonSchema { get; } = metadataSource.ReturnJsonSchema;

            // Never expose mod-owned MethodInfo or JsonSerializerOptions (which pin collectible ALCs);
            // inherit host-owned AIFunction.JsonSerializerOptions (AIJsonUtilities.DefaultOptions).
            public override MethodInfo? UnderlyingMethod => null;

            protected override async ValueTask<object?> InvokeCoreAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                using var lease = registration.EnterInvocation();
                cancellationToken.ThrowIfCancellationRequested();
                if (!weakSource.TryGetTarget(out var target))
                {
                    throw new InvalidOperationException("This chat plugin has been unloaded.");
                }

                return await target.Function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
