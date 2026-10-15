using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Everywhere.Patches.SemanticKernel;

/// <summary>
/// Stores model-facing descriptions with a weak reference to the executable function.
/// This type is transplanted into SemanticKernel.Abstractions by the build-time patcher.
/// </summary>
internal sealed class KernelFunctionProxy : KernelFunction
{
    private readonly WeakReference<KernelFunction> _target;
    private readonly Func<IDisposable> _enterInvocation;

    /// <summary>
    /// Builds a proxy from detached metadata and an activation-specific admission callback.
    /// </summary>
    private KernelFunctionProxy(
        KernelFunctionMetadata metadata,
        WeakReference<KernelFunction> target,
        Func<IDisposable> enterInvocation
    ) : base(
        metadata.Name,
        metadata.PluginName,
        metadata.Description,
        metadata.Parameters,
        metadata.ReturnParameter,
        additionalMetadata: new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(metadata.AdditionalProperties)))
    {
        _target = target;
        _enterInvocation = enterInvocation;
    }

    /// <summary>
    /// Materializes SK's schemas without retaining their CLR metadata or serializer options.
    /// Defaults and shallow-copied additional property values retain caller-provided references that can prevent unloading.
    /// </summary>
    internal static KernelFunction Create(KernelFunction target, Func<IDisposable> enterInvocation)
    {
        var metadata = new KernelFunctionMetadata(target.Name)
        {
            PluginName = target.PluginName,
            Description = target.Description,
            AdditionalProperties = target.Metadata.AdditionalProperties,
            Parameters = target.Metadata.Parameters.Select(parameter => new KernelParameterMetadata(parameter.Name)
            {
                Description = parameter.Description,
                IsRequired = parameter.IsRequired,
                DefaultValue = parameter.DefaultValue,
                Schema = parameter.Schema
            }).ToArray(),
            ReturnParameter = new KernelReturnParameterMetadata
            {
                Description = target.Metadata.ReturnParameter.Description,
                Schema = target.Metadata.ReturnParameter.Schema
            }
        };

        return new KernelFunctionProxy(metadata, new WeakReference<KernelFunction>(target), enterInvocation);
    }

    /// <inheritdoc />
    public override KernelFunction Clone(string? pluginName = null)
    {
        var metadata = new KernelFunctionMetadata(Metadata) { PluginName = pluginName };
        return new KernelFunctionProxy(metadata, _target, _enterInvocation);
    }

    /// <summary>
    /// Keeps a target alive throughout the outer SK pipeline, including filters and streaming disposal.
    /// Admission and retirement are serialized by the registration callback.
    /// </summary>
    internal IDisposable EnterInvocation()
    {
        var target = GetTarget();
        return new Invocation(target, _enterInvocation());
    }

    /// <inheritdoc />
    protected override async ValueTask<FunctionResult> InvokeCoreAsync(
        Kernel kernel,
        KernelArguments arguments,
        CancellationToken cancellationToken)
    {
        var target = CloneTarget();
        var result = await patch_KernelFunction.InvokeTargetCoreAsync(target, kernel, arguments, cancellationToken).ConfigureAwait(false);
        return new FunctionResult(result) { Function = this };
    }

    /// <inheritdoc />
    protected override IAsyncEnumerable<TResult> InvokeStreamingCoreAsync<TResult>(
        Kernel kernel,
        KernelArguments arguments,
        CancellationToken cancellationToken)
    {
        return patch_KernelFunction.InvokeTargetStreamingCoreAsync<TResult>(CloneTarget(), kernel, arguments, cancellationToken);
    }

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        using var invocation = EnterInvocation();
        return await patch_KernelFunction.InvokeTargetCoreAsync(CloneTarget(), arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A short-lived clone preserves snapshot aliases for injected KernelFunction parameters and custom implementations.
    /// It never escapes through FunctionResult.Function.
    /// </summary>
    private KernelFunction CloneTarget()
    {
        var original = GetTarget();
        var target = original.Clone(PluginName);
        target.Metadata.Name = Name;
        target.Kernel = Kernel ?? original.Kernel;
        return target;
    }

    /// <summary>
    /// Obtains a temporary strong reference without allowing the snapshot to own the implementation.
    /// </summary>
    private KernelFunction GetTarget()
    {
        if (!_target.TryGetTarget(out var target))
        {
            throw new InvalidOperationException("This chat plugin has been unloaded.");
        }

        return target;
    }

    /// <summary>
    /// Releases execution ownership even if callers retain a completed task or disposed enumerator.
    /// </summary>
    private sealed class Invocation(KernelFunction target, IDisposable registrationLease) : IDisposable
    {
        private KernelFunction? _target = target;
        private IDisposable? _registrationLease = registrationLease;

        /// <inheritdoc />
        public void Dispose()
        {
            var lease = Interlocked.Exchange(ref _registrationLease, null);
            GC.KeepAlive(_target);
            _target = null;
            lease?.Dispose();
        }
    }
}