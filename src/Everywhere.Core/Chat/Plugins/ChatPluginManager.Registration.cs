using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
    /// Owns a mod tool activation. Discovery exposes the real plugin; chat snapshots receive revocable
    /// wrappers whose references back to mod delegates and metadata clear immediately upon retirement.
    /// </summary>
    private sealed class ChatPluginRegistration(ModChatPlugin plugin, ChatPluginManager manager) : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Keeps active function wrappers alive only as long as the revocable proxy survives and this
        // registration remains open, preventing unbounded growth if dynamic tools are created per turn.
        private ConditionalWeakTable<RevocableAIFunction, NativeChatFunction>? _targets = new();
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
                var retiredPlugin = _plugin;
                _plugin = null;
                _targets = null;

                try
                {
                    // DI may dispose discovery before the mod entry during normal application exit.
                    if (!manager._isDisposed && retiredPlugin is not null) manager._nativePluginsSource.Remove(retiredPlugin);
                }
                finally
                {
                    if (_activeCalls == 0) _drained.TrySetResult();
                }

                return new ValueTask(_drained.Task);
            }
        }

        /// <summary>
        /// Admits a discovery pass while this registration is still open.
        /// </summary>
        public bool TryEnterDiscovery([NotNullWhen(true)] out IDisposable? lease)
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

        /// <summary>
        /// Registers <paramref name="source"/> for this activation and returns a revocable host proxy
        /// wrapper, or <see langword="false"/> if the registration closed concurrently during discovery.
        /// </summary>
        public bool TryCreateFunction(NativeChatFunction source, [NotNullWhen(true)] out NativeChatFunction? function)
        {
            lock (_gate)
            {
                if (_isClosed || _targets is null)
                {
                    function = null;
                    return false;
                }

                var revocable = new RevocableAIFunction(source.Function, this);
                _targets.Add(revocable, source);
                function = new NativeChatFunction(
                    revocable,
                    source,
                    source.BeforeCallHook is null ? null : BeforeCall,
                    source.PermissionConsentHook is null ? null : InspectConsent);
                return true;

                // The closures retain only this registration and the revocable host proxy, never mod delegates.
                ChatPluginDisplayBlock? BeforeCall(FunctionCallContent content)
                {
                    if (!TryEnterInvocation(revocable, out var target, out var lease))
                    {
                        return null;
                    }

                    using (lease)
                    {
                        return target.BeforeCallHook?.Invoke(content);
                    }
                }

                bool? InspectConsent(FunctionCallContext context, PermissionConsentRequest request)
                {
                    if (!TryEnterInvocation(revocable, out var target, out var lease))
                    {
                        return false;
                    }

                    using (lease)
                    {
                        return target.PermissionConsentHook?.Invoke(context, request);
                    }
                }
            }
        }

        private bool TryEnterInvocation(
            RevocableAIFunction proxy,
            [NotNullWhen(true)] out NativeChatFunction? target,
            [NotNullWhen(true)] out IDisposable? lease
        )
        {
            lock (_gate)
            {
                if (_isClosed || _targets is null || !_targets.TryGetValue(proxy, out target))
                {
                    target = null;
                    lease = null;
                    return false;
                }

                _activeCalls++;
                lease = Disposable.Create(ReleaseInvocation);
                return true;
            }
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
        /// Revocable <see cref="AIFunction"/> proxy that copies schema and identity eagerly without
        /// retaining references to the mod's <see cref="NativeChatFunction"/> after retirement.
        /// </summary>
        private sealed class RevocableAIFunction(
            AIFunction metadataSource,
            ChatPluginRegistration registration
        ) : AIFunction
        {
            public override string Name { get; } = metadataSource.Name;

            public override string Description { get; } = metadataSource.Description;

            public override JsonElement JsonSchema { get; } = metadataSource.JsonSchema;

            public override JsonElement? ReturnJsonSchema { get; } = metadataSource.ReturnJsonSchema;

            protected override async ValueTask<object?> InvokeCoreAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken
            )
            {
                if (!registration.TryEnterInvocation(this, out var target, out var lease))
                {
                    throw new InvalidOperationException("This chat plugin has been unloaded.");
                }

                using (lease)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return await target.Function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
