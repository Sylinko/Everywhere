using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using MonoMod;

namespace Everywhere.Patches.SemanticKernel;

/// <summary>
/// Adds revocable functions without replacing Semantic Kernel's invocation pipeline.
/// The original public methods still own filters, diagnostics, cancellation and streaming.
/// Only proxy calls acquire a registration lease around that entire pipeline.
/// </summary>
[MonoModPatch("Microsoft.SemanticKernel.KernelFunction")]
internal class patch_KernelFunction
{
    /// <summary>
    /// Creates a host-owned description whose executable target is weakly referenced.
    /// Core accesses this added method through UnsafeAccessor, keeping compile references unpatched.
    /// </summary>
    internal static KernelFunction CreateRevocableProxy(KernelFunction target, Func<IDisposable> enterInvocation)
    {
        return KernelFunctionProxy.Create(target, enterInvocation);
    }

    /// <summary>
    /// Preserves the original invocation and keeps post-invocation filters inside the lease.
    /// </summary>
    public async Task<FunctionResult> InvokeAsync(Kernel kernel, KernelArguments? arguments, CancellationToken cancellationToken)
    {
        using var invocation = ((object)this as KernelFunctionProxy)?.EnterInvocation();
        var result = await orig_InvokeAsync(kernel, arguments, cancellationToken).ConfigureAwait(false);
        return (object)this is KernelFunctionProxy proxy ? new FunctionResult(result) { Function = proxy } : result;
    }

    /// <summary>
    /// Preserves the original streaming pipeline through enumerator completion or disposal.
    /// </summary>
    public async IAsyncEnumerable<TResult> InvokeStreamingAsync<TResult>(
        Kernel kernel,
        KernelArguments? arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var invocation = ((object)this as KernelFunctionProxy)?.EnterInvocation();
        await foreach (var item in orig_InvokeStreamingAsync<TResult>(kernel, arguments, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Calls the target's virtual execution core without starting another filter pipeline.
    /// </summary>
    internal static ValueTask<FunctionResult> InvokeTargetCoreAsync(
        KernelFunction target,
        Kernel kernel,
        KernelArguments arguments,
        CancellationToken cancellationToken)
    {
        return ((patch_KernelFunction)(object)target).InvokeCoreAsync(kernel, arguments, cancellationToken);
    }

    /// <summary>
    /// Preserves the target's existing AIFunction argument and result handling.
    /// </summary>
    internal static ValueTask<object?> InvokeTargetCoreAsync(
        KernelFunction target,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        return ((patch_KernelFunction)(object)target).InvokeCoreAsync(arguments, cancellationToken);
    }

    /// <summary>
    /// Forwards streaming to the target's virtual implementation.
    /// </summary>
    internal static IAsyncEnumerable<TResult> InvokeTargetStreamingCoreAsync<TResult>(
        KernelFunction target,
        Kernel kernel,
        KernelArguments arguments,
        CancellationToken cancellationToken)
    {
        return ((patch_KernelFunction)(object)target).InvokeStreamingCoreAsync<TResult>(kernel, arguments, cancellationToken);
    }

    /// <summary>
    /// Binds to the original non-streaming pipeline during weaving.
    /// </summary>
    private extern Task<FunctionResult> orig_InvokeAsync(Kernel kernel, KernelArguments? arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Binds to the original streaming pipeline during weaving.
    /// </summary>
    private extern IAsyncEnumerable<TResult> orig_InvokeStreamingAsync<TResult>(
        Kernel kernel,
        KernelArguments? arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Maps the existing abstract execution slot without modifying it.
    /// </summary>
    [MonoModIgnore]
    protected virtual extern ValueTask<FunctionResult> InvokeCoreAsync(Kernel kernel, KernelArguments arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Maps the existing AIFunction execution slot without modifying it.
    /// </summary>
    [MonoModIgnore]
    protected virtual extern ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Maps the existing abstract streaming slot without modifying it.
    /// </summary>
    [MonoModIgnore]
    protected virtual extern IAsyncEnumerable<TResult> InvokeStreamingCoreAsync<TResult>(
        Kernel kernel,
        KernelArguments arguments,
        CancellationToken cancellationToken);
}